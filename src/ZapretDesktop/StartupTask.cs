using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace ZapretDesktop;

internal static class StartupTask
{
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int CreateOrUpdateWithoutPrincipalAce = 6 | 16;
    private const int InteractiveToken = 3;
    private const int HighestRunLevel = 1;
    private const int LogonTrigger = 9;
    private const int ExecuteAction = 0;
    private const int IgnoreNewInstance = 2;
    private const string LauncherName = "ZapretDesktopStartup.exe";
    private const string AppName = "ZapretDesktop.exe";
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string AppRoot = Path.Combine(ProgramFiles, "Zapret Desktop");

    public static string UserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("Не удалось определить учётную запись Windows.");
    }

    public static bool IsEnabled(string sid)
    {
        dynamic folder = Connect().GetFolder("\\");
        dynamic? task = Find(folder, TaskName(sid));
        return task is not null && (bool)task.Enabled;
    }

    public static void SetEnabled(string sid, bool enabled)
    {
        if (sid != UserSid())
            throw new InvalidOperationException("Настройте автозапуск из учётной записи администратора, в которой будете работать.");

        dynamic service = Connect();
        dynamic folder = service.GetFolder("\\");
        string name = TaskName(sid);
        if (!enabled)
        {
            if (Find(folder, name) is not null)
                folder.DeleteTask(name, 0);
            Preferences.RemoveLegacyAutostart();
            return;
        }

        string launcher = PrepareLauncher();
        dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = "Запускает сохранённый пресет Zapret Desktop при входе в Windows.";
        task.Principal.UserId = sid;
        task.Principal.LogonType = InteractiveToken;
        task.Principal.RunLevel = HighestRunLevel;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.MultipleInstances = IgnoreNewInstance;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        dynamic trigger = task.Triggers.Create(LogonTrigger);
        trigger.UserId = sid;
        trigger.Delay = "PT10S";
        dynamic action = task.Actions.Create(ExecuteAction);
        action.Path = launcher;
        action.WorkingDirectory = Path.GetDirectoryName(launcher);

        string permissions = $"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;{sid})";
        folder.RegisterTaskDefinition(name, task, CreateOrUpdateWithoutPrincipalAce, sid, null, InteractiveToken, permissions);
        Preferences.RemoveLegacyAutostart();
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic? Find(dynamic folder, string name)
    {
        try { return folder.GetTask(name); }
        catch (COMException error) when (error.HResult == FileNotFound) { return null; }
    }

    private static string TaskName(string sid) => "Zapret Desktop " + sid;

    private static string PrepareLauncher()
    {
        using var launcher = typeof(StartupTask).Assembly.GetManifestResourceStream("ZapretDesktop.Startup.exe") ??
            throw new InvalidOperationException("Для автозапуска используйте опубликованный ZapretDesktop.exe.");
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден файл приложения.");
        using var app = File.OpenRead(source);
        string digest = Convert.ToHexString(SHA256.HashData(app));
        app.Position = 0;

        CheckDirectory(new DirectoryInfo(ProgramFiles));
        CreateTrustedDirectory(AppRoot);
        string startup = Path.Combine(AppRoot, "startup");
        CreateTrustedDirectory(startup);
        string directory = Path.Combine(startup, digest[..24]);
        CreateTrustedDirectory(directory);

        CopyToProtectedPath(app, Path.Combine(directory, AppName));
        string path = Path.Combine(directory, LauncherName);
        CopyToProtectedPath(launcher, path);
        return path;
    }

    private static void CopyToProtectedPath(Stream source, string path)
    {
        if (File.Exists(path))
        {
            CheckFile(new FileInfo(path));
            using var existing = File.OpenRead(path);
            if (!SHA256.HashData(existing).SequenceEqual(SHA256.HashData(source)))
                throw new IOException("Файл автозапуска повреждён. Удалите папку автозапуска и повторите настройку.");
            return;
        }

        string staging = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(file);
            CheckFile(new FileInfo(staging));
            File.Move(staging, path);
        }
        finally { File.Delete(staging); }
    }

    public static void CheckEngineFiles(EngineFiles engine)
    {
        CheckDirectory(new DirectoryInfo(ProgramFiles));
        CheckDirectory(new DirectoryInfo(AppRoot));
        string engineDirectory = Path.Combine(AppRoot, "engine");
        CheckDirectory(new DirectoryInfo(engineDirectory));
        CheckFile(new FileInfo(Path.Combine(engineDirectory, "current.txt")));
        CheckTree(new DirectoryInfo(engine.Root));
    }

    private static void CheckTree(DirectoryInfo directory)
    {
        CheckDirectory(directory);
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if (item is DirectoryInfo child)
                CheckTree(child);
            else if (item is FileInfo file)
                CheckFile(file);
        }
    }

    private static void CreateTrustedDirectory(string path) => CheckDirectory(Directory.CreateDirectory(path));

    private static void CheckDirectory(DirectoryInfo directory)
    {
        CheckLink(directory);
        CheckPermissions(directory, directory.GetAccessControl());
    }

    private static void CheckFile(FileInfo file)
    {
        CheckLink(file);
        CheckPermissions(file, file.GetAccessControl());
    }

    private static void CheckLink(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw UnsafePath(item);
    }

    private static void CheckPermissions(FileSystemInfo item, FileSystemSecurity permissions)
    {
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership | (FileSystemRights)0x50000000;
        var sidType = typeof(SecurityIdentifier);
        if (permissions.GetOwner(sidType) is not SecurityIdentifier owner || !IsTrusted(owner))
            throw UnsafePath(item);
        foreach (FileSystemAccessRule rule in permissions.GetAccessRules(true, true, sidType))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !IsTrusted((SecurityIdentifier)rule.IdentityReference))
                throw UnsafePath(item);
        }
    }

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller

    private static IOException UnsafePath(FileSystemInfo item) =>
        new($"Автозапуску нужна защищённая папка Program Files. Проверьте права для «{item.FullName}».");
}
