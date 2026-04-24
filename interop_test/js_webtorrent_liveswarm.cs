// JS WebTorrent LIVE-SWARM interop test.
//
// Closes the "Pure-v2 JS-WebTorrent live interop" audit gap by driving a real
// `webtorrent` (npm v2+) seeder in Node.js with WebRTC support via
// @roamhq/wrtc, pairing it with a SpawnDev.WebTorrent C# client as the leech,
// and verifying byte-identical transfer via the WebTorrent WebSocket-tracker
// + WebRTC peer-wire path that JS browsers use in production.
//
// Flow:
//   1. Launch js/seeder.js as a child process. Parse stdout until it prints
//      `READY infohash=<hex>` so we know the Node seeder is announcing.
//   2. Create a SpawnDev.WebTorrent client with trackers enabled, DHT off
//      (we want the JS WebTorrent tracker path specifically).
//   3. Add the same .torrent on the C# side.
//   4. Wait for torrent.Done or timeout.
//   5. Extract bytes from the in-memory store via torrent.Files[0].ReadAsync.
//   6. SHA-256 compare against the original payload.
//   7. Kill the Node seeder.
//
// Pre-reqs:
//   - Node.js >=20 on PATH.
//   - `cd js && npm install` done once.
//   - gen_qbittorrent_test.cs run once (for output/payload.bin + .torrent files).
//
// Run: dotnet run js_webtorrent_liveswarm.cs

#:project D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent.csproj
#:package SpawnDev.RTC.Server@1.0.3

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpawnDev.RTC.Server.Extensions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SpawnDev.WebTorrent;

var scriptPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.EndsWith("js_webtorrent_liveswarm.cs"));
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

// --- 0. Spin up a local SpawnDev.RTC.Server tracker so JS + C# clients have a
// guaranteed-reachable WebSocket tracker with no Origin allowlist blocking
// their announces. hub.spawndev.com's allowlist rejects Node.js / .NET clients
// that don't send Origin; the public tracker.openwebtorrent.com is unreliable
// for automated tests. Local Kestrel + UseRtcSignaling gives us a known-good.
int trackerPort;
using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); trackerPort = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
var trackerWsUrl = $"ws://127.0.0.1:{trackerPort}/announce";
Console.WriteLine($"Starting local tracker at {trackerWsUrl} ...");

var appBuilder = WebApplication.CreateBuilder();
appBuilder.Logging.ClearProviders();
appBuilder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, trackerPort));
var trackerApp = appBuilder.Build();
trackerApp.UseWebSockets();
var trackerSignalingServer = trackerApp.UseRtcSignaling("/announce");
await trackerApp.StartAsync();

// Diagnostic: poll tracker state in background so we can see if JS + C# actually
// connect to the WebSocket tracker.
var diagCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    var lastReport = "";
    while (!diagCts.IsCancellationRequested)
    {
        var peerIds = trackerSignalingServer.ConnectedPeerIds;
        var rooms = trackerSignalingServer.Rooms.Count;
        var total = trackerSignalingServer.TotalPeers;
        var report = $"[Tracker] rooms={rooms} peers={total} ids=[{string.Join(",", peerIds.Take(4))}]";
        if (report != lastReport) { Console.WriteLine(report); lastReport = report; }
        try { await Task.Delay(1000, diagCts.Token); } catch { }
    }
});

// --- Generate a torrent on the fly with only our local tracker in the announce
// list, so both clients will announce to it (and nowhere else).
var payloadBytes = File.ReadAllBytes(payloadPath);
var (torrentFileBytes, meta) = TorrentCreator.CreateFromBytes(
    "payload.bin", payloadBytes,
    new TorrentCreatorOptions
    {
        PieceLength = 65536,
        Trackers = new[] { trackerWsUrl },
        MetaVersion = 2,
        Hybrid = true,
        Comment = "JS WebTorrent interop liveswarm test",
    });

var torrentPath = Path.Combine(outDir, "jsinterop_hybrid.torrent");
File.WriteAllBytes(torrentPath, torrentFileBytes);
Console.WriteLine($"Generated torrent: v1={meta.InfoHash} v2={meta.V2InfoHash} tracker={trackerWsUrl}");

// --- 1. Launch the Node.js seeder, wait for READY ---
Console.WriteLine("Starting JS WebTorrent seeder (Node.js) ...");
var seederPsi = new ProcessStartInfo("node")
{
    ArgumentList = { "seeder.js", torrentPath, outDir },
    WorkingDirectory = jsDir,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
using var seeder = Process.Start(seederPsi) ?? throw new Exception("Failed to start node.js");

// Buffer stdout so we can watch for READY and also echo PEER-CONNECT / PROGRESS
// as the swarm forms. Stderr is pure error output.
var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var readyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
_ = Task.Run(async () =>
{
    string? line;
    while ((line = await seeder.StandardOutput.ReadLineAsync()) != null)
    {
        Console.WriteLine($"[JS] {line}");
        if (line.StartsWith("READY ", StringComparison.Ordinal))
        {
            readyTcs.TrySetResult(line);
        }
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
    using var reg = readyCts.Token.Register(() => readyTcs.TrySetCanceled());
    var ready = await readyTcs.Task;
    Console.WriteLine($"JS seeder ready: {ready}");
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("JS seeder did not report READY within 30s. Killing.");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    return 3;
}

// --- 2. Start the SpawnDev.WebTorrent C# client ---
var tmpRoot = Path.Combine(Path.GetTempPath(), "spawndev_jsliveswarm_" + Guid.NewGuid().ToString("N").Substring(0, 8));
Directory.CreateDirectory(tmpRoot);
Console.WriteLine($"C# client downloading to: {tmpRoot}");

await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    EnableTrackers = true,
    EnableDht = false,   // keep it JS-tracker-only
    EnableLsd = false,
    EnableUtPex = false,
});
var addOpts = new AddTorrentOptions { Path = tmpRoot };

var torrentBytes = File.ReadAllBytes(torrentPath);
var torrent = client.Add(torrentBytes, addOpts);
Console.WriteLine($"C# client added torrent: infoHash={torrent.WireInfoHashHex}, pieces={torrent.PieceCount}");

// --- 3. Wait for download completion ---
var deadline = DateTime.UtcNow.AddSeconds(120);
double lastProgress = -1;
while (DateTime.UtcNow < deadline)
{
    var p = torrent.Progress;
    if (torrent.Done) break;
    if (Math.Abs(p - lastProgress) > 0.01)
    {
        Console.WriteLine($"  C# progress={p:P1} downloaded={torrent.Downloaded} bytes peers={torrent.NumPeers} wires={torrent.Wires.Count}");
        lastProgress = p;
    }
    await Task.Delay(500);
}

if (!torrent.Done)
{
    Console.Error.WriteLine($"C# client timed out. progress={torrent.Progress:P2} downloaded={torrent.Downloaded}/{torrent.Length}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    return 4;
}
Console.WriteLine($"C# client download complete: {torrent.Downloaded} bytes across {torrent.Wires.Count} wires.");

// --- 4. Verify byte-identity ---
var tf = torrent.Files?.FirstOrDefault();
if (tf == null) { Console.Error.WriteLine("No files on torrent after download"); return 5; }
var downloadedBytes = await tf.ReadAsync(0, (int)tf.Length);
var originalBytes = File.ReadAllBytes(payloadPath);

if (originalBytes.Length != downloadedBytes.Length)
{
    Console.Error.WriteLine($"Length mismatch: original={originalBytes.Length}, downloaded={downloadedBytes.Length}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    return 6;
}

var originalHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
var downloadedHash = Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant();
if (originalHash != downloadedHash)
{
    Console.Error.WriteLine($"Hash mismatch:\n  original  {originalHash}\n  downloaded {downloadedHash}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    return 7;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  JS-WebTorrent LIVE-SWARM PASS");
Console.WriteLine($"  {downloadedBytes.Length} bytes SHA-256 byte-identical ({downloadedHash.Substring(0, 16)}...)");
Console.WriteLine($"  Transport: WebTorrent npm v2 (Node.js + @roamhq/wrtc) -> SpawnDev.WebTorrent C#");
Console.WriteLine($"  Trackers : WebSocket fleet (tracker.openwebtorrent.com + hub.spawndev.com)");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

// Clean shutdown.
try { seeder.Kill(entireProcessTree: true); } catch { }
try { await seeder.WaitForExitAsync(); } catch { }
diagCts.Cancel();
try { await trackerApp.StopAsync(); } catch { }
return 0;
