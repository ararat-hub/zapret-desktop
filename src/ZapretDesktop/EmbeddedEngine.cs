using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZapretDesktop;

internal static class EmbeddedEngine
{
    private const string ArchiveName = "ZapretDesktop.Flowseal.zip";
    private const string ManifestName = "ZapretDesktop.Flowseal.json";
    private static readonly Assembly App = typeof(EmbeddedEngine).Assembly;

    public static Release? Release { get; } = ReadManifest();

    public static byte[]? TryRead(Release release)
    {
        if (release != Release)
            return null;

        using var archive = App.GetManifestResourceStream(ArchiveName)
            ?? throw new InvalidDataException("Встроенный архив отсутствует.");
        var bytes = new byte[release.Size];
        archive.ReadExactly(bytes);
        if (archive.ReadByte() != -1 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(release.Sha256)))
            throw new InvalidDataException("Встроенный архив не прошёл проверку SHA-256.");
        return bytes;
    }

    private static Release? ReadManifest()
    {
        using var stream = App.GetManifestResourceStream(ManifestName);
        if (stream is null)
            return null;

        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        string tag = root.GetProperty("tag").GetString()!;
        string sha256 = root.GetProperty("sha256").GetString()!;
        int size = root.GetProperty("size").GetInt32();
        if (Releases.ParseVersion(tag) is not { } version || !Releases.ValidHash(sha256) ||
            size is <= 0 or > 32 * 1024 * 1024)
            throw new InvalidDataException("Некорректные данные встроенного архива.");
        return new Release(tag, version, sha256.ToUpperInvariant(), size);
    }
}
