using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ZapretDesktop;

internal enum Targets { Both, Discord, YouTube }

internal sealed record ProbeResult(bool? Discord, bool? YouTube)
{
    public bool Passed => Discord != false && YouTube != false;
    public override string ToString() => string.Join(" · ",
        new[] { Describe("Discord", Discord), Describe("YouTube", YouTube) }.OfType<string>());
    private static string? Describe(string service, bool? ok) =>
        ok is null ? null : $"{service} {(ok.Value ? "✓" : "×")}";
}

internal static class Connectivity
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.Zero,
        UseProxy = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(4),
        MaxResponseContentBufferSize = 128 * 1024,
        DefaultRequestHeaders = { UserAgent = { new("ZapretDesktop", "0.1") } }
    };

    public static async Task<ProbeResult> CheckAsync(Targets targets, CancellationToken token)
    {
        var discord = targets == Targets.YouTube
            ? Task.FromResult<bool?>(null)
            : ProbeAsync("https://discord.com/api/v10/gateway", IsDiscordGateway, token);
        var youtube = targets == Targets.Discord
            ? Task.FromResult<bool?>(null)
            : ProbeAsync("https://www.youtube.com/robots.txt",
                bytes => Encoding.UTF8.GetString(bytes).Contains("User-agent:", StringComparison.Ordinal), token);
        await Task.WhenAll(discord, youtube);
        return new ProbeResult(await discord, await youtube);
    }

    private static async Task<bool?> ProbeAsync(string url, Func<byte[], bool> validate, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.ConnectionClose = true;
            request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
            using var response = await Client.SendAsync(request, token);
            return response.IsSuccessStatusCode && validate(await response.Content.ReadAsByteArrayAsync(token));
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return false;
        }
    }

    private static bool IsDiscordGateway(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.ValueKind == JsonValueKind.Object &&
            json.RootElement.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(url.GetString(), UriKind.Absolute, out var address) &&
            address.Scheme == "wss" && address.Host == "gateway.discord.gg";
    }
}
