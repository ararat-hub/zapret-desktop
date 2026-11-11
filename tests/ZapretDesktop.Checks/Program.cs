using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ZapretDesktop;

using var temp = Directory.CreateTempSubdirectory("zapret-checks-").WithCleanup();
var release = new Release("1.10.4", new Version(1, 10, 4), new string('A', 64), 3);
string Metadata(string url, string digest, bool prerelease = false) => JsonSerializer.Serialize(new
{
    tag_name = release.Tag, draft = false, prerelease,
    assets = new[] { new { name = "zapret-discord-youtube-1.10.4.zip", size = 3, digest, browser_download_url = url } }
});
Release Parse(string json)
{
    using var document = JsonDocument.Parse(json);
    return Releases.ReadMetadata(document.RootElement, release.Tag);
}

Assert(Parse(Metadata(release.Download.AbsoluteUri, "sha256:" + release.Sha256)) == release, "release metadata");
Reject(() => Parse(Metadata("https://example.com/file.zip", "sha256:" + release.Sha256)), "foreign download URL");
Reject(() => Parse(Metadata(release.Download.AbsoluteUri, "")), "missing SHA-256");
Reject(() => Parse(Metadata(release.Download.AbsoluteUri, "sha256:" + release.Sha256, true)), "prerelease");

byte[] payload = [1, 2, 3];
var matchingRelease = release with { Sha256 = Convert.ToHexString(SHA256.HashData(payload)) };
using var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host == "github.com"
    ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new("https://release-assets.githubusercontent.com/test") } }
    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
Assert((await Releases.DownloadAsync(http, matchingRelease, default)).SequenceEqual(payload), "verified CDN download");
try
{
    await Releases.DownloadAsync(http, release, default);
    throw new Exception("Expected checksum failure");
}
catch (InvalidDataException) { }

const string presetText = """
    @echo off
    start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=443,%GameFilterTCP% ^
    --hostlist="%LISTS%a list.txt" --marker=^! --new
    echo ignored
    """;
var store = new EngineStore(temp.Info.FullName);
var engine = store.Commit(release, CreatePackage(release.Tag, presetText), default);
Assert(store.ReadCurrent()?.Root == engine.Root && engine.Presets.Count == 1, "install and reload engine");
var preset = engine.Presets.Single();
Assert(preset.Arguments.Count == 4 && PresetParser.Expand(preset.Arguments[0], engine.Root) == "--wf-tcp=443,12",
    "expand variable and ignore unrelated commands");
Assert(PresetParser.Expand(preset.Arguments[1], engine.Root) == "--hostlist=" + Path.Combine(engine.Root, "lists", "a list.txt"),
    "preserve spaces in arguments");
Assert(preset.Arguments[2] == "--marker=!", "translate escaped exclamation mark");

var newer = release with { Tag = "1.10.5", Version = new(1, 10, 5) };
Reject(() => store.Commit(newer, CreatePackage(newer.Tag, presetText.Replace("%LISTS%", "%UNKNOWN%")), default),
    "reject incompatible update");
Assert(store.ReadCurrent()?.Root == engine.Root, "failed update preserves working engine");
Reject(() => store.Commit(newer, CreatePackage(newer.Tag, presetText, "../../escape.txt"), default), "reject ZIP traversal");
Assert(store.ReadCurrent()?.Root == engine.Root, "traversal cannot replace engine");
var update = store.Commit(newer, CreatePackage(newer.Tag, presetText), default);
Assert(store.ReadCurrent()?.Root == update.Root && Directory.Exists(engine.Root), "commit update and retain active version");
Assert(store.Commit(release, CreatePackage(release.Tag, presetText), default).Root == update.Root, "no downgrade");
File.Delete(Path.Combine(update.Root, "bin", "winws.exe"));
Assert(store.Commit(newer, CreatePackage(newer.Tag, presetText), default).Root != update.Root, "repair a broken install");

if (args is ["--live-release"])
{
    var liveStore = new EngineStore(Path.Combine(temp.Info.FullName, "official"));
    var latest = await Releases.CheckAsync(default);
    var installed = await liveStore.InstallAsync(latest, default);
    int officialPresets = Directory.GetFiles(installed.Root, "general*.bat").Length;
    Assert(installed.Presets.Count == officialPresets, "parse all official presets");
    Console.WriteLine($"Verified official release {latest.Tag} with {officialPresets} presets.");
}
Console.WriteLine("All checks passed.");

static void Assert(bool passed, string name)
{
    if (!passed)
        throw new Exception($"Failed: {name}");
}

static void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or IOException) { return; }
    throw new Exception($"Expected rejection: {name}");
}

static byte[] CreatePackage(string tag, string preset, string? extra = null)
{
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
    {
        string root = $"zapret-discord-youtube-{tag}/";
        foreach (string file in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" })
            zip.CreateEntry(root + "bin/" + file);
        zip.CreateEntry(root + "lists/");
        using (var writer = new StreamWriter(zip.CreateEntry(root + "general.bat").Open()))
            writer.Write(preset);
        if (extra is not null)
            zip.CreateEntry(extra);
    }
    return bytes.ToArray();
}

sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(response(request));
}

sealed class Scratch(DirectoryInfo info) : IDisposable
{
    public DirectoryInfo Info => info;
    public void Dispose() => info.Delete(recursive: true);
}

static class ScratchExtensions
{
    public static Scratch WithCleanup(this DirectoryInfo directory) => new(directory);
}
