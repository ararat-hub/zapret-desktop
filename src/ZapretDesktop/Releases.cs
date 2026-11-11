using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretDesktop;

internal sealed record Release(string Tag, Version Version, string Sha256, int Size)
{
    public const string Project = "https://github.com/Flowseal/zapret-discord-youtube";
    public Uri Download => new($"{Project}/releases/download/{Tag}/zapret-discord-youtube-{Tag}.zip");
}

internal static partial class Releases
{
    private const int MaxZipSize = 32 * 1024 * 1024;
    private const string Api = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases";
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 2 * 1024 * 1024,
        DefaultRequestHeaders = { UserAgent = { new("ZapretDesktop", "0.1") } }
    };

    [GeneratedRegex(@"\Av?[0-9]{1,6}(\.[0-9]{1,6}){2,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    public static Version? ParseVersion(string? tag) =>
        tag is not null && TagPattern().IsMatch(tag) && Version.TryParse(tag.TrimStart('v'), out var version)
            ? version : null;

    public static bool ValidHash(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    public static async Task<Release> CheckAsync(CancellationToken token, string? tag = null)
    {
        if (tag is not null && ParseVersion(tag) is null)
            throw new InvalidDataException("Неверный тег релиза.");

        using var request = new HttpRequestMessage(HttpMethod.Get, tag is null ? $"{Api}/latest" : $"{Api}/tags/{tag}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub ограничил число запросов. Попробуйте позже.");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        return ReadMetadata(json.RootElement, tag);
    }

    internal static Release ReadMetadata(JsonElement root, string? requestedTag)
    {
        string? tag = root.GetProperty("tag_name").GetString();
        var version = ParseVersion(tag);
        if (tag is null || version is null || (requestedTag is not null && tag != requestedTag) ||
            root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub вернул неподходящий релиз.");

        string name = $"zapret-discord-youtube-{tag}.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name)
                continue;
            var digest = asset.GetProperty("digest").GetString();
            int size = asset.GetProperty("size").GetInt32();
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !ValidHash(digest[7..]) || size is <= 0 or > MaxZipSize)
                break;

            var release = new Release(tag, version, digest[7..].ToUpperInvariant(), size);
            if (asset.GetProperty("browser_download_url").GetString() == release.Download.AbsoluteUri)
                return release;
            break;
        }
        throw new InvalidDataException("У релиза нет подходящего ZIP с контрольной суммой SHA-256.");
    }

    public static async Task<byte[]> DownloadAsync(Release release, CancellationToken token)
        => await DownloadAsync(Client, release, token);

    internal static async Task<byte[]> DownloadAsync(HttpClient client, Release release, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        Uri url = release.Download;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { IsAbsoluteUri: true } next)
            {
                if (next.Scheme != Uri.UriSchemeHttps || !next.IsDefaultPort || next.UserInfo.Length != 0 ||
                    next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("GitHub перенаправил загрузку на неизвестный сервер.");
                url = next;
                continue;
            }

            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[release.Size];
            await stream.ReadExactlyAsync(bytes, timeout.Token);
            if (await stream.ReadAsync(new byte[1], timeout.Token) != 0 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(release.Sha256)))
                throw new InvalidDataException("Контрольная сумма или размер ZIP не совпадает.");
            return bytes;
        }
        throw new InvalidDataException("Не удалось получить архив по ссылке GitHub.");
    }

    internal static void Extract(byte[] bytes, string staging, CancellationToken token)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 2000 || zip.Entries.Sum(entry => entry.Length) > 128L * 1024 * 1024)
            throw new InvalidDataException("Архив превышает допустимый размер распаковки.");
        token.ThrowIfCancellationRequested();
        zip.ExtractToDirectory(staging);
        token.ThrowIfCancellationRequested();
    }
}
