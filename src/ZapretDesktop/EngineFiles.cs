using System.IO;
using System.Text.RegularExpressions;

namespace ZapretDesktop;

internal sealed record EngineFiles(string Tag, Version Version, string Root, IReadOnlyList<Preset> Presets);
internal sealed record Preset(string Name, IReadOnlyList<string> Arguments);

internal sealed class EngineStore(string directory)
{
    public static EngineStore Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zapret Desktop", "engine"));

    public EngineFiles? ReadCurrent()
    {
        string marker = Path.Combine(directory, "current.txt");
        if (!File.Exists(marker))
            return null;
        var parts = File.ReadAllText(marker).Trim().Split('/');
        if (parts.Length != 2 || Releases.ParseVersion(parts[0]) is not { } version ||
            !Guid.TryParseExact(parts[1], "N", out _))
            throw new InvalidDataException("Неверная версия установленного движка.");
        return ReadEngine(parts[0], version, Path.Combine(directory, $"{parts[0]}-{parts[1]}"));
    }

    public async Task<EngineFiles> InstallAsync(Release release, CancellationToken token)
    {
        if (ReadHealthyEngine() is { } current && release.Version <= current.Version)
            return current;

        byte[] bytes = await Releases.DownloadAsync(release, token);
        return await Task.Run(() => Commit(release, bytes, token), token);
    }

    internal EngineFiles Commit(Release release, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var installLock = new FileStream(Path.Combine(directory, "install.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if (ReadHealthyEngine() is { } current && release.Version <= current.Version)
            return current;

        string staging = Path.Combine(directory, ".stage-" + Guid.NewGuid().ToString("N"));
        string marker = Path.Combine(staging, "current.txt");
        string installId = Guid.NewGuid().ToString("N");
        string final = Path.Combine(directory, $"{release.Tag}-{installId}");
        Directory.CreateDirectory(staging);
        try
        {
            Releases.Extract(bytes, staging, token);
            string package = Path.Combine(staging, $"zapret-discord-youtube-{release.Tag}");
            if (!Directory.Exists(package))
                throw new InvalidDataException("Изменилась структура архива Flowseal.");

            AddDefaultList(package, "ipset-exclude-user.txt", "203.0.113.113/32\n");
            AddDefaultList(package, "list-general-user.txt", "# User list\ndomain.example.abc\n");
            AddDefaultList(package, "list-exclude-user.txt", "domain.example.abc\n");
            var engine = ReadEngine(release.Tag, release.Version, package);
            token.ThrowIfCancellationRequested();

            Directory.Move(package, final);
            File.WriteAllText(marker, $"{release.Tag}/{installId}");
            File.Move(marker, Path.Combine(directory, "current.txt"), overwrite: true);
            return engine with { Root = final };
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private EngineFiles? ReadHealthyEngine()
    {
        try { return ReadCurrent(); }
        catch (InvalidDataException) { return null; }
    }

    private static EngineFiles ReadEngine(string tag, Version version, string root)
    {
        foreach (var file in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" })
            if (!File.Exists(Path.Combine(root, "bin", file)))
                throw new InvalidDataException($"В движке отсутствует bin\\{file}.");
        if (!Directory.Exists(Path.Combine(root, "lists")))
            throw new InvalidDataException("В движке отсутствуют списки доменов.");

        var presets = Directory.EnumerateFiles(root, "general*.bat")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(PresetParser.Read)
            .OfType<Preset>()
            .ToArray();
        if (presets.Length == 0)
            throw new InvalidDataException("В движке нет совместимых пресетов general*.bat.");
        return new EngineFiles(tag, version, root, presets);
    }

    private static void AddDefaultList(string package, string name, string contents)
    {
        string path = Path.Combine(package, "lists", name);
        if (!File.Exists(path))
            File.WriteAllText(path, contents);
    }
}

internal static partial class PresetParser
{
    [GeneratedRegex("""^\s*start\s+"[^"]*"\s+/min\s+"%BIN%winws\.exe"\s+(?<args>.*)$""", RegexOptions.IgnoreCase)]
    private static partial Regex StartLine();

    [GeneratedRegex("""(?:[^\s"]+|"[^"]*")+""")]
    private static partial Regex Tokens();

    [GeneratedRegex(@"%[^%]+%")]
    private static partial Regex Variable();

    private static Preset? Parse(string name, string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            var match = StartLine().Match(lines[index]);
            if (!match.Success)
                continue;

            string text = match.Groups["args"].Value.TrimEnd();
            while (text.EndsWith('^') && ++index < lines.Length)
                text = text[..^1] + " " + lines[index].Trim();
            text = text.Replace("^!", "!", StringComparison.Ordinal);
            if (text.IndexOfAny(['^', '&', '|', '<', '>']) >= 0 || text.Count(c => c == '"') % 2 != 0)
                return null;

            var args = Tokens().Matches(text).Select(token => token.Value.Replace("\"", "")).ToArray();
            bool ValidToken(string arg) => arg.StartsWith("--", StringComparison.Ordinal) &&
                !Variable().Replace(arg, variable => variable.Value switch
                {
                    "%BIN%" or "%LISTS%" or "%GameFilter%" or "%GameFilterTCP%" or "%GameFilterUDP%" => "",
                    _ => "%",
                }).Contains('%');
            return args.Length > 0 && args.All(ValidToken) ? new Preset(name, args) : null;
        }
        return null;
    }

    public static Preset? Read(string file) => Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllLines(file));

    public static string Expand(string argument, string root) => Variable().Replace(argument, variable => variable.Value switch
    {
        "%BIN%" => Path.Combine(root, "bin") + Path.DirectorySeparatorChar,
        "%LISTS%" => Path.Combine(root, "lists") + Path.DirectorySeparatorChar,
        "%GameFilter%" or "%GameFilterTCP%" or "%GameFilterUDP%" => "12",
        _ => throw new InvalidDataException("Неизвестная переменная в пресете."),
    });
}
