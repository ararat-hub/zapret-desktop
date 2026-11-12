using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZapretDesktop;

public partial class MainWindow : Window
{
    private readonly EngineStore store = EngineStore.Default();
    private readonly EngineSession session = new();
    private readonly Preferences preferences = Preferences.Load();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(4) };
    private readonly Queue<string> log = new();
    private readonly bool elevated;
    private readonly Release? embedded = EmbeddedEngine.Release;
    private readonly LocalEngine? local = LocalEngine.Discover();
    private CancellationTokenSource? operation;
    private TaskCompletionSource? operationFinished;
    private EngineFiles? engine;
    private Release? offer;
    private string? activePreset;
    private bool checking;
    private bool searching;
    private bool closing;
    private bool ready;

    public MainWindow(string[] arguments)
    {
        using var identity = WindowsIdentity.GetCurrent();
        elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        InitializeComponent();
        LicenseButton.Visibility = embedded is null ? Visibility.Collapsed : Visibility.Visible;
        TargetBox.SelectedIndex = (int)preferences.Targets;
        AutoUpdateBox.IsChecked = preferences.AutoCheck;
        try { AutostartBox.IsChecked = Preferences.HasAutostart(); }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            AutostartBox.IsEnabled = false;
            Report("Автозапуск недоступен", error);
        }
        session.Exited += process => Dispatcher.BeginInvoke(() => OnEngineExit(process));
        updateTimer.Tick += async (_, _) =>
        {
            if (operation is null && preferences.AutoCheck)
                await CheckUpdatesAsync();
        };
        Closing += OnClosing;

        SetStatus("Выключено", "Подберите пресет для своей сети или выберите его вручную.");
        UpdateText.Text = local is not null ? "Локальные файлы готовы к использованию." :
            embedded is null ? "Обновления из официальных релизов Flowseal." : $"В комплекте Zapret {embedded.Tag}.";
        try { SetEngine(store.ReadCurrent()); }
        catch (Exception error)
        {
            SetEngine(null);
            Report("Не удалось прочитать установленный движок", error);
        }
        ready = true;
        RenderControls();
        Loaded += async (_, _) =>
        {
            updateTimer.Start();
            await ResumeActionAsync(arguments);
            if (!closing && preferences.AutoCheck && offer is null)
                await CheckUpdatesAsync();
        };
    }

    private Task ResumeActionAsync(string[] args)
    {
        if (!elevated)
            return Task.CompletedTask;
        return args switch
        {
            ["--connect"] => RunAsync(SelectPresetAsync, search: true),
            ["--manual", var name] => RunAsync(token => StartManualAsync(name, token)),
            ["--local-install" or "--local-connect", var path, var tag, var digest] =>
                RunAsync(async token =>
                {
                    var snapshot = LocalEngine.ReadHandoff(path, tag, digest);
                    await InstallLocalAsync(snapshot, args[0] == "--local-connect", token);
                }, search: args[0] == "--local-connect"),
            ["--update" or "--install-connect", var tag, var digest]
                when Releases.ParseVersion(tag) is not null && Releases.ValidHash(digest) =>
                RunAsync(async token =>
                {
                    var release = embedded is { } bundled && bundled.Tag == tag &&
                        string.Equals(bundled.Sha256, digest, StringComparison.OrdinalIgnoreCase)
                        ? bundled : await Releases.CheckAsync(token, tag);
                    if (!string.Equals(release.Sha256, digest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Архив релиза изменился. Подтвердите установку заново.");
                    await InstallAsync(release, token);
                    if (args[0] == "--install-connect")
                        await SelectPresetAsync(token);
                }, search: args[0] == "--install-connect"),
            _ => Task.CompletedTask,
        };
    }

    private async void PrimaryClick(object sender, RoutedEventArgs e)
    {
        if (searching)
        {
            operation?.Cancel();
            return;
        }
        if (activePreset is not null || session.HasPendingCleanup)
        {
            await RunAsync(_ =>
            {
                activePreset = null;
                SetStatus("Выключено", "Подберите пресет для своей сети или выберите его вручную.");
                ShowChecks(null);
                return Task.CompletedTask;
            });
            return;
        }
        if (engine is null)
        {
            if (local is not null)
            {
                await ConfirmLocalAsync(local, connect: true);
                return;
            }
            var release = embedded ?? offer ?? await CheckUpdatesAsync();
            if (release is not null && !closing)
                await ConfirmInstallAsync(release, connect: true);
            return;
        }
        if (EnsureElevated("--connect"))
            await RunAsync(SelectPresetAsync, search: true);
    }

    private async void ManualClick(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is string name && EnsureElevated("--manual", name))
            await RunAsync(token => StartManualAsync(name, token));
    }

    private async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (AvailableUpdate is { } release)
            await ConfirmInstallAsync(release, connect: false);
        else
            await CheckUpdatesAsync();
    }

    private async void LocalEngineClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку Zapret с service.bat и bin" };
        if (local is not null)
            picker.InitialDirectory = local.Root;
        if (picker.ShowDialog(this) != true)
            return;
        try
        {
            var source = LocalEngine.Read(picker.FolderName) ??
                throw new InvalidDataException("В этой папке не найдены service.bat, bin и пресеты Flowseal.");
            await ConfirmLocalAsync(source, connect: false);
        }
        catch (Exception error)
        {
            UpdateText.Text = error.Message;
            Report("Локальная папка", error);
        }
    }

    private async Task ConfirmLocalAsync(LocalEngine source, bool connect)
    {
        if (MessageBox.Show(this, $"Использовать Zapret {source.Tag} из папки?\n{source.Root}\n\n" +
            "Файлы будут скопированы в папку приложения для запуска с правами администратора.",
            "Локальная копия Zapret", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunAsync(async token =>
        {
            Log($"Читаем локальный Zapret {source.Tag}.");
            var snapshot = await Task.Run(() => source.MakeArchive(token), token);
            if (elevated)
            {
                await InstallLocalAsync(snapshot, connect, token);
                return;
            }
            string path = LocalEngine.SaveHandoff(snapshot.Bytes);
            EnsureElevated(connect ? "--local-connect" : "--local-install", path,
                snapshot.Release.Tag, snapshot.Release.Sha256);
        }, search: connect);
    }

    private async Task InstallLocalAsync((Release Release, byte[] Bytes) snapshot, bool connect, CancellationToken token)
    {
        UpdateText.Text = $"Копируем локальный Zapret {snapshot.Release.Tag}…";
        var installed = await Task.Run(() => store.Commit(snapshot.Release, snapshot.Bytes, token, replace: true), token);
        SetEngine(installed);
        if (offer is { } pending && pending.Version <= installed.Version)
            offer = null;
        UpdateText.Text = $"Используется локальный Zapret {installed.Tag}.";
        Log($"Движок {installed.Tag} импортирован из локальной папки.");
        if (connect)
            await SelectPresetAsync(token);
    }

    private async Task ConfirmInstallAsync(Release release, bool connect)
    {
        string title = engine is null ? "Установить движок Zapret?" : "Обновить движок Zapret?";
        string detail = activePreset is null ? "" : "\nАктивный пресет продолжит работать на текущей версии до остановки.";
        string source = release == embedded ? "из этого EXE" : "из релиза Flowseal";
        string action = release == embedded ? "Установить" : "Скачать и установить";
        if (MessageBox.Show(this, $"{action} Zapret {release.Tag} {source}?{detail}",
            title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        if (EnsureElevated(connect ? "--install-connect" : "--update", release.Tag, release.Sha256))
            await RunAsync(async token =>
            {
                await InstallAsync(release, token);
                if (connect)
                    await SelectPresetAsync(token);
            }, search: connect);
    }

    private async Task<Release?> CheckUpdatesAsync()
    {
        if (checking || operation is not null || closing)
            return null;
        checking = true;
        UpdateText.Text = "Проверяем релизы Flowseal…";
        RenderControls();
        try
        {
            var release = await Releases.CheckAsync(lifetime.Token);
            var currentVersion = engine?.Version ?? embedded?.Version;
            offer = currentVersion is null || release.Version > currentVersion ? release : null;
            UpdateText.Text = offer is not null
                ? engine is null ? $"Доступна версия {release.Tag}." : $"Доступно обновление {release.Tag}."
                : engine is not null ? "Установлена актуальная версия."
                : local is not null ? "Локальные файлы готовы к использованию."
                : "Актуальный пакет встроен. Готов к установке.";
            return offer;
        }
        catch (Exception error)
        {
            if (!closing)
            {
                UpdateText.Text = engine is null && (local is not null || embedded is not null)
                    ? "Установка доступна без GitHub."
                    : "Не удалось проверить GitHub. Повторите позже.";
                Report("Проверка обновлений", error);
            }
            return offer;
        }
        finally
        {
            checking = false;
            if (!closing)
                RenderControls();
        }
    }

    private async Task InstallAsync(Release release, CancellationToken token)
    {
        Log(release == embedded ? $"Распаковываем встроенный Zapret {release.Tag}." : $"Загружаем Zapret {release.Tag}.");
        UpdateText.Text = $"Устанавливаем версию {release.Tag}…";
        try
        {
            SetEngine(await store.InstallAsync(release, token));
            offer = offer is { } pending && pending.Version > engine!.Version ? offer : null;
            UpdateText.Text = activePreset is null
                ? $"Установлена версия {engine!.Tag}."
                : $"Версия {engine!.Tag} готова. Будет использована при следующем подключении.";
            Log($"Движок {engine.Tag} готов.");
        }
        catch
        {
            if (!closing)
                UpdateText.Text = "Установка прервалась. Можно попробовать ещё раз.";
            throw;
        }
    }

    private async Task SelectPresetAsync(CancellationToken token)
    {
        var current = engine ?? throw new InvalidOperationException("Сначала установите движок.");
        ShowChecks(null);
        await SuspendLegacyServiceAsync(token);
        var match = await PresetSelection.FindAsync(current, session, preferences.Targets, preferences.LastPreset,
            detail => SetStatus("Подбор пресета", detail), Log, ShowChecks, token);
        token.ThrowIfCancellationRequested();
        if (match is { } selected)
        {
            SetConnected(current, selected.Preset, "Проверки выбранных сервисов пройдены.");
            return;
        }
        SetStatus("Пресет не найден", "Выберите один сервис, проверьте журнал или запустите пресет вручную.");
    }

    private async Task StartManualAsync(string name, CancellationToken token)
    {
        var current = engine ?? throw new InvalidOperationException("Сначала установите движок.");
        var preset = current.Presets.FirstOrDefault(preset => preset.Name == name) ??
            throw new InvalidOperationException("Этот пресет отсутствует в установленной версии.");
        ShowChecks(null);
        await SuspendLegacyServiceAsync(token);
        await session.StartAsync(current, preset, token);
        SetConnected(current, preset, "Пресет запущен вручную.");
        var result = await Connectivity.CheckAsync(preferences.Targets, token);
        if (session.IsRunning)
        {
            ShowChecks(result);
            Log($"{preset.Name} · {result}");
        }
    }

    private async Task SuspendLegacyServiceAsync(CancellationToken token)
    {
        SetStatus("Подготовка подключения", "Проверяем, запущена ли служба zapret…");
        if (await session.SuspendLegacyServiceAsync(token))
            Log("Служба zapret временно остановлена.");
    }

    private void SetConnected(EngineFiles current, Preset preset, string detail)
    {
        activePreset = preset.Name;
        preferences.LastPreset = preset.Name;
        SavePreferences();
        SetStatus("Включено", $"{preset.Name} · Zapret {current.Tag}\n{detail}", connected: true);
        Log($"Запущен {preset.Name} · Zapret {current.Tag}.");
    }

    private async void OnEngineExit(Process process)
    {
        if (closing || !session.Owns(process) || activePreset is null)
            return;
        Log($"winws завершился с кодом {process.ExitCode}.");
        activePreset = null;
        ShowChecks(null);
        SetStatus("Подключение остановлено", "winws завершился. Подберите пресет заново или проверьте журнал.");
        await RunAsync(_ => Task.CompletedTask);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing)
        {
            e.Cancel = true;
            return;
        }

        if (operation is null && !session.HasPendingCleanup)
        {
            closing = true;
            updateTimer.Stop();
            lifetime.Cancel();
            session.Dispose();
            return;
        }

        e.Cancel = true;
        closing = true;
        RenderControls();
        SetStatus("Завершение подключения", "Останавливаем пресет и восстанавливаем службу zapret…");
        operation?.Cancel();
        await Task.Yield();
        if (operationFinished is { } pending)
            await pending.Task;

        activePreset = null;
        ShowChecks(null);
        bool finished = await FinishSessionAsync();
        closing = false;
        if (finished)
            Close();
        else
            RenderControls();
    }

    private async Task<bool> FinishSessionAsync()
    {
        if (!session.HasPendingCleanup)
            return true;
        try
        {
            if (await session.EndAsync())
                Log("Служба zapret снова запущена.");
            return true;
        }
        catch (Exception error)
        {
            SetStatus("Не удалось завершить подключение", error.Message);
            Report("Восстановление подключения", error);
            return false;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool search = false)
    {
        if (operation is not null || closing)
            return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operationFinished = finished;
        operation = cancellation;
        searching = search;
        RenderControls();
        try { await action(cancellation.Token); }
        catch (Exception error)
        {
            if (!closing)
            {
                if (activePreset is null)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        ShowChecks(null);
                        SetStatus("Подбор отменён", "Можно начать подбор заново или выбрать пресет вручную.");
                    }
                    else
                        SetStatus("Не удалось выполнить действие", error.Message);
                }
                if (!cancellation.IsCancellationRequested)
                    Report("Ошибка", error);
            }
        }
        finally
        {
            searching = false;
            if (!closing && activePreset is null)
            {
                RenderControls();
                await FinishSessionAsync();
            }
            operation = null;
            operationFinished = null;
            finished.SetResult();
            if (!closing)
                RenderControls();
        }
    }

    private bool EnsureElevated(params string[] arguments)
    {
        if (elevated)
            return true;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("--handoff");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Не удалось перезапустить приложение.");
            Close();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            Log("Запрос прав администратора отменён.");
        }
        catch (Exception error)
        {
            Report("Запуск с правами администратора", error);
        }
        return false;
    }

    private void ReleaseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo($"{Release.Project}/releases/latest")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error) { Report("Открытие страницы релизов", error); }
    }

    private void LicenseClick(object sender, RoutedEventArgs e)
    {
        using var notice = typeof(MainWindow).Assembly.GetManifestResourceStream("ZapretDesktop.ThirdParty.txt");
        if (notice is null)
            return;
        using var reader = new StreamReader(notice);
        var dialog = new Window
        {
            Owner = this,
            Title = "Лицензии компонентов Zapret Desktop",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 740,
            Height = 540,
            Background = (Brush)FindResource("Canvas"),
            Content = new TextBox
            {
                Margin = new Thickness(18),
                Padding = new Thickness(12),
                Background = (Brush)FindResource("Inset"),
                Foreground = (Brush)FindResource("Ink"),
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                IsReadOnly = true,
                Text = reader.ReadToEnd(),
            },
        };
        dialog.ShowDialog();
    }

    private void TargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || TargetBox.SelectedIndex < 0)
            return;
        preferences.Targets = (Targets)TargetBox.SelectedIndex;
        ShowChecks(null);
        SavePreferences();
    }

    private void AutostartClick(object sender, RoutedEventArgs e)
    {
        try { Preferences.SetAutostart(AutostartBox.IsChecked == true); }
        catch (Exception error)
        {
            AutostartBox.IsChecked = AutostartBox.IsChecked != true;
            Report("Автозапуск", error);
        }
    }

    private async void AutoUpdateClick(object sender, RoutedEventArgs e)
    {
        preferences.AutoCheck = AutoUpdateBox.IsChecked == true;
        SavePreferences();
        if (preferences.AutoCheck)
            await CheckUpdatesAsync();
    }

    private void SavePreferences()
    {
        try { preferences.Save(); }
        catch (Exception error) { Report("Сохранение настроек", error); }
    }

    private void SetEngine(EngineFiles? value)
    {
        engine = value;
        EngineVersion.Text = value is not null ? $"Zapret {value.Tag}" :
            local is not null ? $"Zapret {local.Tag} найден" :
            embedded is not null ? $"Zapret {embedded.Tag} встроен" : "Требуется установка";
        var names = value?.Presets.Select(preset => preset.Name).ToArray() ?? [];
        PresetBox.ItemsSource = names;
        int saved = Array.IndexOf(names, preferences.LastPreset);
        PresetBox.SelectedIndex = saved >= 0 ? saved : 0;
    }

    private void SetStatus(string title, string detail, bool connected = false)
    {
        StatusTitle.Text = title;
        StatusText.Text = detail;
        StatusDot.Fill = (Brush)FindResource(connected ? "Accent" : "Muted");
    }

    private void ShowChecks(ProbeResult? result)
    {
        SetCheck(DiscordCheck, result?.Discord);
        SetCheck(YouTubeCheck, result?.YouTube);
    }

    private void SetCheck(TextBlock text, EndpointCheck? check)
    {
        text.Text = check is null ? "Ожидает проверки" : check.Passed ? "Доступен" : "Ошибка проверки";
        text.Foreground = (Brush)FindResource(check is null ? "Muted" : check.Passed ? "Accent" : "Attention");
        text.ToolTip = check?.Detail;
    }

    private Release? AvailableUpdate => offer ??
        (engine is not null && embedded is not null && embedded.Version > engine.Version ? embedded : null);

    private void RenderControls()
    {
        bool busy = closing || operation is not null;
        bool connected = activePreset is not null;
        bool restoring = !connected && session.HasPendingCleanup;
        PrimaryButton.Content = searching ? "Отменить подбор" : connected ? "Остановить" :
            restoring ? "Повторить восстановление" : engine is null ? "Установить и подобрать" : "Подобрать и включить";
        PrimaryButton.IsEnabled = !closing && (searching || (!busy &&
            (restoring || engine is not null || local is not null || embedded is not null || !checking)));
        TargetBox.IsEnabled = !busy && !connected && !restoring;
        PresetBox.IsEnabled = ManualButton.IsEnabled = engine is not null && !busy && !connected && !restoring;
        UpdateButton.IsEnabled = !busy && !checking;
        LocalEngineButton.IsEnabled = !busy && !connected && !restoring;
        UpdateButton.Content = AvailableUpdate is { } update ? $"Установить {update.Tag}" : "Проверить обновления";
        Progress.IsIndeterminate = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
    }

    private void Report(string context, Exception error) => Log($"{context} · {error.Message}");

    private void Log(string message)
    {
        if (log.Count == 80)
            log.Dequeue();
        log.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        Activity.Text = string.Join(Environment.NewLine, log);
        Activity.ScrollToEnd();
    }
}
