// JS WebTorrent LIVE-SWARM interop test against the LIVE HUB.
//
// Variant of js_webtorrent_liveswarm.cs that targets wss://hub.spawndev.com:44365/announce
// instead of spawning a local SpawnDev.RTC.ServerApp subprocess. Verifies that JS
// `webtorrent@^2` (Node.js + @roamhq/wrtc) and SpawnDev.WebTorrent C# can transfer
// bytes through the production hub end-to-end.
//
// Flow:
//   1. Generate a fresh hybrid torrent pointing only at the live hub.
//   2. Launch js/seeder.js as the JS WebTorrent seeder; wait for READY.
//   3. Create a SpawnDev.WebTorrent C# client, add the torrent, let Discovery
//      announce to the live hub and receive offers.
//   4. Wait for torrent.Done or timeout.
//   5. Read bytes via in-memory ReadAsync, SHA-256-verify against original.
//   6. Clean shutdown of seeder.
//
// Pre-reqs:
//   - Node.js >=20 on PATH.
//   - `cd js && npm install` done once.
//   - `gen_qbittorrent_test.cs` run once for output/payload.bin.
//   - hub.spawndev.com reachable.
//
// Run: dotnet run js_webtorrent_liveswarm_hub.cs

#:project D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using SpawnDev.WebTorrent;

const string HubWss = "wss://hub.spawndev.com:44365/announce";
const string HubBase = "https://hub.spawndev.com:44365";

var scriptPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.EndsWith("js_webtorrent_liveswarm_hub.cs"));
var scriptDir = scriptPath != null ? Path.GetDirectoryName(Path.GetFullPath(scriptPath))! : Directory.GetCurrentDirectory();
var outDir = Path.Combine(scriptDir, "output");
var jsDir = Path.Combine(scriptDir, "js");

var payloadPath = Path.Combine(outDir, "payload.bin");
if (!File.Exists(payloadPath)) { Console.Error.WriteLine($"Missing {payloadPath}. Run gen_qbittorrent_test.cs first."); return 2; }
if (!File.Exists(Path.Combine(jsDir, "node_modules", "webtorrent", "package.json")))
{
    Console.Error.WriteLine($"Missing Node.js deps. Run `cd {jsDir} && npm install` first.");
    return 2;
}

// --- 0. Hub sanity check (cheap) ---
using (var sanityHttp = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }))
{
    sanityHttp.Timeout = TimeSpan.FromSeconds(10);
    try
    {
        var hubInfo = await sanityHttp.GetStringAsync(HubBase + "/");
        Console.WriteLine($"Hub reachable: {hubInfo}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Hub unreachable: {ex.Message}");
        return 3;
    }
}

// --- 1. Generate a torrent pointing only at the live hub ---
var payloadBytes = File.ReadAllBytes(payloadPath);
var (torrentFileBytes, meta) = TorrentCreator.CreateFromBytes(
    "payload.bin", payloadBytes,
    new TorrentCreatorOptions
    {
        PieceLength = 65536,
        Trackers = new[] { HubWss },
        MetaVersion = 2,
        Hybrid = true,
        Comment = "JS WebTorrent interop liveswarm against live hub",
    });

var torrentPath = Path.Combine(outDir, "jsinterop_hybrid_hub.torrent");
File.WriteAllBytes(torrentPath, torrentFileBytes);
Console.WriteLine($"Generated torrent: v1={meta.InfoHash} v2={meta.V2InfoHash} tracker={HubWss}");

// --- 2. Launch the Node.js seeder, wait for READY ---
Console.WriteLine("Starting JS WebTorrent seeder (Node.js) ...");
var seederPsi = new ProcessStartInfo("node")
{
    ArgumentList = { "seeder.js", torrentPath, outDir },
    WorkingDirectory = jsDir,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
};
seederPsi.Environment["DEBUG"] = "bittorrent-tracker:websocket-tracker";

using var seeder = Process.Start(seederPsi) ?? throw new Exception("Failed to start node.js");
var readyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
_ = Task.Run(async () =>
{
    string? line;
    while ((line = await seeder.StandardOutput.ReadLineAsync()) != null)
    {
        Console.WriteLine($"[JS] {line}");
        if (line.StartsWith("READY ", StringComparison.Ordinal))
            readyTcs.TrySetResult(line);
    }
});
_ = Task.Run(async () =>
{
    string? line;
    while ((line = await seeder.StandardError.ReadLineAsync()) != null)
        Console.Error.WriteLine($"[JS stderr] {line}");
});

try
{
    using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var reg = readyCts.Token.Register(() => readyTcs.TrySetCanceled());
    await readyTcs.Task;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("JS seeder did not report READY within 30s.");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    return 4;
}

// --- 3. Start the SpawnDev.WebTorrent C# client ---
var tmpRoot = Path.Combine(Path.GetTempPath(), "spawndev_jsliveswarm_hub_" + Guid.NewGuid().ToString("N").Substring(0, 8));
Directory.CreateDirectory(tmpRoot);
Console.WriteLine($"C# client downloading to: {tmpRoot}");

await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    EnableTrackers = true,
    EnableDht = false,
    EnableLsd = false,
    EnableUtPex = false,
    // Empty default trackers - the torrent's own announce list (the live hub) is the only tracker.
    DefaultTrackers = Array.Empty<string>(),
});
var addOpts = new AddTorrentOptions { Path = tmpRoot };

var torrentBytes = File.ReadAllBytes(torrentPath);
var torrent = client.Add(torrentBytes, addOpts);
Console.WriteLine($"C# client added torrent: infoHash={torrent.WireInfoHashHex}, pieces={torrent.PieceCount}");

// --- 4. Wait for download completion. Also poll hub /stats so we can see the swarm. ---
using var statsHttp = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
statsHttp.Timeout = TimeSpan.FromSeconds(5);
var deadline = DateTime.UtcNow.AddSeconds(120);
double lastProgress = -1;
int ticks = 0;
while (DateTime.UtcNow < deadline)
{
    if (torrent.Done) break;
    var p = torrent.Progress;
    if (Math.Abs(p - lastProgress) > 0.01 || ticks % 10 == 0)
    {
        Console.WriteLine($"  C# progress={p:P1} downloaded={torrent.Downloaded} bytes peers={torrent.NumPeers} wires={torrent.Wires.Count}");
        lastProgress = p;
        try
        {
            var stats = await statsHttp.GetStringAsync($"{HubBase}/stats");
            Console.WriteLine($"  [Hub /stats] {stats}");
        }
        catch (Exception ex) { Console.WriteLine($"  [Hub /stats] err: {ex.Message}"); }
    }
    await Task.Delay(500);
    ticks++;
}

if (!torrent.Done)
{
    Console.Error.WriteLine($"C# client timed out. progress={torrent.Progress:P2} downloaded={torrent.Downloaded}/{torrent.Length} peers={torrent.NumPeers} wires={torrent.Wires.Count}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    return 5;
}
Console.WriteLine($"C# client download complete: {torrent.Downloaded} bytes across {torrent.Wires.Count} wires.");

// --- 5. Verify ---
var tf = torrent.Files?.FirstOrDefault();
if (tf == null) { Console.Error.WriteLine("No files on torrent after download"); return 6; }
var downloadedBytes = await tf.ReadAsync(0, (int)tf.Length);
var originalBytes = payloadBytes;

if (originalBytes.Length != downloadedBytes.Length)
{
    Console.Error.WriteLine($"Length mismatch: original={originalBytes.Length}, downloaded={downloadedBytes.Length}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    return 7;
}

var originalHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
var downloadedHash = Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant();
if (originalHash != downloadedHash)
{
    Console.Error.WriteLine($"Hash mismatch:\n  original  {originalHash}\n  downloaded {downloadedHash}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    return 8;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  JS-WebTorrent LIVE-SWARM (LIVE HUB) PASS");
Console.WriteLine($"  {downloadedBytes.Length} bytes SHA-256 byte-identical ({downloadedHash.Substring(0, 16)}...)");
Console.WriteLine($"  Transport: webtorrent@^2 (Node.js + @roamhq/wrtc) -> SpawnDev.WebTorrent C#");
Console.WriteLine($"  Tracker : LIVE hub.spawndev.com ({HubWss})");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

try { seeder.Kill(entireProcessTree: true); } catch { }
try { await seeder.WaitForExitAsync(); } catch { }
return 0;
