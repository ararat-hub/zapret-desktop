using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZapretDesktop;

internal sealed record LocalEngine(string Root, string Tag, Version Version)
{
    private const int MaxBytes = 32 * 1024 * 1024;

    public static LocalEngine? Discover()
    {
        foreach (string service in new[] { "zapret", "WinDivert" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
                if (key?.GetValue("ImagePath") is string path && FindRoot(path) is { } root && Read(root) is { } found)
                    return found;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { }
        }

        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path && FindRoot(path) is { } root && Read(root) is { } found)
                        return found;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or
                    IOException or UnauthorizedAccessException)
                { }
            }
        }
        return null;
    }

    internal static string? FindRoot(string imagePath)
    {
        var match = Regex.Match(imagePath.TrimStart('"'), @"^(?:\\\?\?\\|\\\\\?\\)?(?<file>[a-z]:\\.*?\\bin\\(?:winws\.exe|WinDivert64\.sys))(?=""|\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? Path.GetDirectoryName(Path.GetDirectoryName(match.Groups["file"].Value)) : null;
    }

    public static LocalEngine? Read(string root)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "bin", "winws.exe")) || !Directory.Exists(Path.Combine(root, "lists")))
            return null;
        var script = new FileInfo(Path.Combine(root, "service.bat"));
        if (!script.Exists || script.Length > 1024 * 1024)
            return null;
        var match = Regex.Match(File.ReadAllText(script.FullName), "^\\s*set\\s+\"LOCAL_VERSION=([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        string tag = match.Groups[1].Value;
        return Releases.ParseVersion(tag) is { } version && Directory.EnumerateFiles(root, "general*.bat").Any()
            ? new LocalEngine(root, tag, version) : null;
    }

    public (Release Release, byte[] Bytes) MakeArchive(CancellationToken token)
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            string prefix = $"zapret-discord-youtube-{Tag}/";
            long remaining = MaxBytes;
            int count = 0;
            foreach (var folder in new[] { "", "bin", "lists" })
            {
                var directory = Path.Combine(Root, folder);
                if (Directory.ResolveLinkTarget(directory, returnFinalTarget: false) is not null)
                    throw new InvalidDataException("Папка Zapret содержит перенаправление.");
                foreach (string file in Directory.EnumerateFiles(directory, folder.Length == 0 ? "general*.bat" : "*"))
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 1000 || File.ResolveLinkTarget(file, returnFinalTarget: false) is not null)
                        throw new InvalidDataException("Не удалось безопасно прочитать папку Zapret.");
                    using var input = File.OpenRead(file);
                    remaining -= input.Length;
                    if (remaining < 0)
                        throw new InvalidDataException("Локальная копия Zapret превышает допустимый размер.");
                    string entry = prefix + (folder.Length == 0 ? "" : folder + "/") + Path.GetFileName(file);
                    using var output = zip.CreateEntry(entry, CompressionLevel.Fastest).Open();
                    input.CopyTo(output);
                }
            }
        }
        if (archive.Length > MaxBytes)
            throw new InvalidDataException("Архив локальной копии превышает допустимый размер.");
        var bytes = archive.ToArray();
        return (new Release(Tag, Version, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length), bytes);
    }

    public static string SaveHandoff(byte[] bytes)
    {
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), $"ZapretDesktop-Import-{id}.zip");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        return path;
    }

    public static (Release Release, byte[] Bytes) ReadHandoff(string path, string tag, string digest)
    {
        if (Path.GetPathRoot(path) is not { Length: 3 } || !Path.IsPathFullyQualified(path) ||
            !Regex.IsMatch(Path.GetFileName(path), @"\AZapretDesktop-Import-[a-f0-9]{32}\.zip\z") ||
            Releases.ParseVersion(tag) is not { } version || !Releases.ValidHash(digest))
            throw new InvalidDataException("Неверные параметры локального пакета.");
        using var file = File.OpenRead(path);
        if (file.Length is <= 0 or > MaxBytes)
            throw new InvalidDataException("Неверный размер локального пакета.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(digest)))
            throw new InvalidDataException("Локальный пакет изменился. Выберите папку Zapret заново.");
        return (new Release(tag, version, digest, bytes.Length), bytes);
    }

}
