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
    private CancellationTokenSource? operation;
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
        Closing += (_, _) =>
        {
            closing = true;
            updateTimer.Stop();
            lifetime.Cancel();
            session.Dispose();
        };

        SetStatus("Выключено", "Подберите пресет для своей сети или выберите его вручную.");
        UpdateText.Text = "Обновления из официальных релизов Flowseal.";
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
            ["--update" or "--install-connect", var tag, var digest]
                when Releases.ParseVersion(tag) is not null && Releases.ValidHash(digest) =>
                RunAsync(async token =>
                {
                    var release = await Releases.CheckAsync(token, tag);
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
        if (activePreset is not null)
        {
            await RunAsync(_ =>
            {
                session.Stop();
                activePreset = null;
                SetStatus("Выключено", "Подберите пресет для своей сети или выберите его вручную.");
                Log("Подключение остановлено.");
                return Task.CompletedTask;
            });
            return;
        }
        if (engine is null)
        {
            var release = offer ?? await CheckUpdatesAsync();
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
        if (offer is { } release)
            await ConfirmInstallAsync(release, connect: false);
        else
            await CheckUpdatesAsync();
    }

    private async Task ConfirmInstallAsync(Release release, bool connect)
    {
        string title = engine is null ? "Установить движок Zapret?" : "Обновить движок Zapret?";
        string detail = activePreset is null ? "" : "\nАктивный пресет продолжит работать на текущей версии до остановки.";
        if (MessageBox.Show(this, $"Скачать и установить Zapret {release.Tag} из релиза Flowseal?{detail}",
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
        if (checking || operation is not null)
            return null;
        checking = true;
        UpdateText.Text = "Проверяем релизы Flowseal…";
        RenderControls();
        try
        {
            var release = await Releases.CheckAsync(lifetime.Token);
            offer = engine is null || release.Version > engine.Version ? release : null;
            UpdateText.Text = offer is null
                ? "Установлена актуальная версия."
                : engine is null ? $"Доступна версия {release.Tag}." : $"Доступно обновление {release.Tag}.";
            return offer;
        }
        catch (Exception error)
        {
            if (!closing)
            {
                UpdateText.Text = "Не удалось проверить GitHub. Повторите позже.";
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
        Log($"Загружаем Zapret {release.Tag}.");
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
        EngineSession.EnsureNoOtherEngine();
        var targets = preferences.Targets;
        SetStatus("Подбор пресета", "Проверяем исходное соединение…");
        var baseline = await Connectivity.CheckAsync(targets, token);
        Log($"Без пресета · {baseline}");
        var presets = current.Presets.OrderByDescending(preset => preset.Name == preferences.LastPreset).ToArray();

        for (int index = 0; index < presets.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var preset = presets[index];
            SetStatus("Подбор пресета", $"{index + 1} из {presets.Length} · {preset.Name}");
            try
            {
                await session.StartAsync(current, preset, token);
            }
            catch (Win32Exception) { throw; }
            catch (InvalidOperationException error)
            {
                Log($"{preset.Name} · {error.Message}");
                continue;
            }

            var result = await Connectivity.CheckAsync(targets, token);
            if (result.Passed && session.IsRunning)
                result = await Connectivity.CheckAsync(targets, token);
            Log($"{preset.Name} · {result}");
            if (result.Passed && session.IsRunning)
            {
                SetConnected(current, preset, $"HTTPS-проверки пройдены · {result}");
                return;
            }
            session.Stop();
        }
        SetStatus("Пресет не найден", "Выберите один сервис, проверьте журнал или запустите пресет вручную.");
    }

    private async Task StartManualAsync(string name, CancellationToken token)
    {
        var current = engine ?? throw new InvalidOperationException("Сначала установите движок.");
        var preset = current.Presets.FirstOrDefault(preset => preset.Name == name) ??
            throw new InvalidOperationException("Этот пресет отсутствует в установленной версии.");
        await session.StartAsync(current, preset, token);
        SetConnected(current, preset, "Пресет запущен вручную.");
        var result = await Connectivity.CheckAsync(preferences.Targets, token);
        if (session.IsRunning)
        {
            SetStatus("Включено", $"{preset.Name} · Zapret {current.Tag}\nПроверка HTTPS · {result}", connected: true);
            Log($"{preset.Name} · {result}");
        }
    }

    private void SetConnected(EngineFiles current, Preset preset, string detail)
    {
        activePreset = preset.Name;
        preferences.LastPreset = preset.Name;
        SavePreferences();
        SetStatus("Включено", $"{preset.Name} · Zapret {current.Tag}\n{detail}", connected: true);
        Log($"Запущен {preset.Name} · Zapret {current.Tag}.");
    }

    private void OnEngineExit(Process process)
    {
        if (closing || !session.Owns(process) || activePreset is null)
            return;
        Log($"winws завершился с кодом {process.ExitCode}.");
        session.Stop();
        activePreset = null;
        SetStatus("Подключение остановлено", "winws завершился. Подберите пресет заново или проверьте журнал.");
        RenderControls();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool search = false)
    {
        if (operation is not null || closing)
            return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
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
                    try { session.Stop(); }
                    catch (Exception stopError) { Report("Остановка", stopError); }
                    if (cancellation.IsCancellationRequested)
                        SetStatus("Подбор отменён", "Можно начать подбор заново или выбрать пресет вручную.");
                    else
                        SetStatus("Не удалось выполнить действие", error.Message);
                }
                if (!cancellation.IsCancellationRequested)
                    Report("Ошибка", error);
            }
        }
        finally
        {
            operation = null;
            searching = false;
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

    private void TargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || TargetBox.SelectedIndex < 0)
            return;
        preferences.Targets = (Targets)TargetBox.SelectedIndex;
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
        EngineVersion.Text = value?.Tag ?? "Требуется установка";
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

    private void RenderControls()
    {
        bool busy = operation is not null;
        bool connected = activePreset is not null;
        PrimaryButton.Content = searching ? "Отменить подбор" : connected ? "Остановить" :
            engine is null ? "Установить и подобрать" : "Подобрать и включить";
        PrimaryButton.IsEnabled = searching || (!busy && (engine is not null || !checking));
        TargetBox.IsEnabled = !busy && !connected;
        PresetBox.IsEnabled = ManualButton.IsEnabled = engine is not null && !busy && !connected;
        UpdateButton.IsEnabled = !busy && !checking;
        UpdateButton.Content = offer is { } update ? $"Установить {update.Tag}" : "Проверить обновления";
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
