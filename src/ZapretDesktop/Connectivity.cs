using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ZapretDesktop;

internal enum Targets { Both, Discord, YouTube }

internal sealed record EndpointCheck(bool Passed, string Detail);

internal sealed record ProbeResult(EndpointCheck? Discord, EndpointCheck? YouTube)
{
    public bool Passed => Discord?.Passed != false && YouTube?.Passed != false;
    public override string ToString() => string.Join(" · ",
        new[] { Describe("Discord", Discord), Describe("YouTube", YouTube) }.OfType<string>());
    private static string? Describe(string service, EndpointCheck? check) =>
        check is null ? null : $"{service} {(check.Passed ? "✓" : "×")} ({check.Detail})";
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
        DefaultRequestHeaders = { UserAgent = { new("ZapretDesktop", "0.2") } }
    };

    public static async Task<ProbeResult> CheckAsync(Targets targets, CancellationToken token)
    {
        var discord = targets == Targets.YouTube ? Task.FromResult<EndpointCheck?>(null) : CheckDiscordAsync(token);
        var youtube = targets == Targets.Discord ? Task.FromResult<EndpointCheck?>(null) : CheckYouTubeAsync(token);
        await Task.WhenAll(discord, youtube);
        return new ProbeResult(await discord, await youtube);
    }

    private static async Task<EndpointCheck?> CheckDiscordAsync(CancellationToken token)
    {
        var api = ProbeAsync("https://discord.com/api/v10/gateway", IsDiscordGateway, token);
        var gateway = CheckGatewayAsync(token);
        await Task.WhenAll(api, gateway);
        return Combine("API", await api, "Gateway", await gateway);
    }

    private static async Task<EndpointCheck?> CheckYouTubeAsync(CancellationToken token)
    {
        var site = ProbeAsync("https://www.youtube.com/robots.txt",
            bytes => Encoding.UTF8.GetString(bytes).Contains("User-agent:", StringComparison.Ordinal), token);
        var cdn = ProbeAsync("https://i.ytimg.com/vi/jNQXAC9IVRw/default.jpg",
            bytes => bytes.Length > 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff, token);
        await Task.WhenAll(site, cdn);
        return Combine("сайт", await site, "обложки", await cdn);
    }

    private static EndpointCheck Combine(string firstName, EndpointCheck first, string secondName, EndpointCheck second) =>
        new(first.Passed && second.Passed, $"{firstName} {first.Detail}, {secondName} {second.Detail}");

    private static async Task<EndpointCheck> ProbeAsync(string url, Func<byte[], bool> validate, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.ConnectionClose = true;
            request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
            using var response = await Client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                return new(false, $"HTTP {(int)response.StatusCode}");
            return validate(await response.Content.ReadAsByteArrayAsync(token))
                ? new(true, "OK") : new(false, "неожиданный ответ");
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return DescribeFailure(error);
        }
    }

    private static async Task<EndpointCheck> CheckGatewayAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new("wss://gateway.discord.gg/?v=10&encoding=json"), Client, timeout.Token);
            byte[] buffer = new byte[2048];
            int length = 0;
            while (length < buffer.Length)
            {
                var frame = await socket.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
                if (frame.MessageType != WebSocketMessageType.Text)
                    break;
                length += frame.Count;
                if (!frame.EndOfMessage)
                    continue;
                using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("op", out var op) &&
                    op.ValueKind == JsonValueKind.Number && op.TryGetInt32(out int code) && code == 10)
                    return new(true, "Hello");
                break;
            }
            return new(false, "нет Hello");
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is WebSocketException or HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return DescribeFailure(error);
        }
    }

    private static EndpointCheck DescribeFailure(Exception error)
    {
        var request = error as HttpRequestException ?? error.InnerException as HttpRequestException;
        return new(false, error is OperationCanceledException ? "тайм-аут" : request?.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "DNS",
            HttpRequestError.SecureConnectionError => "TLS",
            HttpRequestError.ConnectionError => "соединение",
            _ => error is JsonException ? "неожиданный ответ" : "нет ответа",
        });
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
