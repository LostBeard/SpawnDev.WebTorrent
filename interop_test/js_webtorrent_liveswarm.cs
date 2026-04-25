// JS WebTorrent LIVE-SWARM interop test.
//
// Closes the "Pure-v2 JS-WebTorrent live interop" audit gap by driving the
// reference JS `webtorrent` (npm v2+) seeder in Node.js with WebRTC support
// via @roamhq/wrtc, pairing it with a SpawnDev.WebTorrent C# client as the
// leech, and verifying byte-identical transfer via the WebTorrent
// WebSocket-tracker + WebRTC peer-wire path that JS browsers use in production.
//
// Flow:
//   1. Launch SpawnDev.RTC.ServerApp as a subprocess on a free port with
//      STUN/TURN disabled (just the tracker). Poll /health until it's ready.
//   2. Generate a fresh hybrid torrent pointing only at that local tracker.
//   3. Launch js/seeder.js (Node.js) with the torrent; wait for READY.
//   4. Create a SpawnDev.WebTorrent C# client, add the torrent, let Discovery
//      announce to the tracker and receive offers.
//   5. Wait for torrent.Done or timeout.
//   6. Read bytes via in-memory ReadAsync, SHA-256-verify against original.
//   7. Clean shutdown of seeder + tracker subprocesses.
//
// Pre-reqs:
//   - Node.js >=20 on PATH.
//   - `cd js && npm install` done once.
//   - SpawnDev.RTC.ServerApp.exe already built in Release (the pre-built binary
//     ships in the SpawnDev.RTC solution).
//   - `gen_qbittorrent_test.cs` run once for output/payload.bin.
//
// Run: dotnet run js_webtorrent_liveswarm.cs

#:project D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent.csproj

using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
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

var serverAppExe = @"D:\users\tj\Projects\SpawnDev.RTC\SpawnDev.RTC\SpawnDev.RTC.ServerApp\bin\Release\net10.0\SpawnDev.RTC.ServerApp.exe";
if (!File.Exists(serverAppExe))
{
    Console.Error.WriteLine($"Missing SpawnDev.RTC.ServerApp.exe at {serverAppExe}.");
    Console.Error.WriteLine("Build it: cd D:\\users\\tj\\Projects\\SpawnDev.RTC\\SpawnDev.RTC\\SpawnDev.RTC.ServerApp && dotnet build -c Release");
    return 2;
}

// --- 0. Spin up SpawnDev.RTC.ServerApp as a subprocess on a free port ---
int trackerPort;
using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); trackerPort = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
var trackerUrl = $"http://127.0.0.1:{trackerPort}";
var trackerWsUrl = $"ws://127.0.0.1:{trackerPort}/announce";
Console.WriteLine($"Starting SpawnDev.RTC.ServerApp at {trackerUrl} ...");

var trackerPsi = new ProcessStartInfo(serverAppExe)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
};
trackerPsi.Environment["ASPNETCORE_URLS"] = trackerUrl;
trackerPsi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";  // silence dev-only launch-settings overrides

using var tracker = Process.Start(trackerPsi) ?? throw new Exception("Failed to start SpawnDev.RTC.ServerApp");
// Tracker stdout -> diagnostic tag so we can see its startup output in context.
_ = Task.Run(async () =>
{
    string? line;
    while ((line = await tracker.StandardOutput.ReadLineAsync()) != null)
        Console.WriteLine($"[Tracker] {line}");
});
_ = Task.Run(async () =>
{
    string? line;
    while ((line = await tracker.StandardError.ReadLineAsync()) != null)
        Console.Error.WriteLine($"[Tracker err] {line}");
});

// Poll /health until the tracker is up (max 15s).
using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            var resp = await http.GetAsync($"{trackerUrl}/health");
            if (resp.IsSuccessStatusCode) break;
        }
        catch { /* not ready yet */ }
        await Task.Delay(300);
    }
    try
    {
        var healthResp = await http.GetAsync($"{trackerUrl}/health");
        if (!healthResp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine("Tracker never reached healthy state.");
            try { tracker.Kill(entireProcessTree: true); } catch { }
            return 3;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Tracker /health error: {ex.Message}");
        try { tracker.Kill(entireProcessTree: true); } catch { }
        return 3;
    }
}
Console.WriteLine("Tracker /health OK.");

// --- 1. Generate a torrent pointing only at the local tracker ---
var payloadBytes = File.ReadAllBytes(payloadPath);
var (torrentFileBytes, meta) = TorrentCreator.CreateFromBytes(
    "payload.bin", payloadBytes,
    new TorrentCreatorOptions
    {
        PieceLength = 65536,
        Trackers = new[] { trackerWsUrl },
        MetaVersion = 2,
        Hybrid = true,
        Comment = "JS WebTorrent interop liveswarm",
    });

var torrentPath = Path.Combine(outDir, "jsinterop_hybrid.torrent");
File.WriteAllBytes(torrentPath, torrentFileBytes);
Console.WriteLine($"Generated torrent: v1={meta.InfoHash} v2={meta.V2InfoHash} tracker={trackerWsUrl}");

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
// Turn on bittorrent-tracker wire-protocol logging so we can see EXACTLY what
// JS sends to the tracker and what it gets back. Goes to stderr which we also
// capture.
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
    try { tracker.Kill(entireProcessTree: true); } catch { }
    return 4;
}

// --- 3. Start the SpawnDev.WebTorrent C# client ---
var tmpRoot = Path.Combine(Path.GetTempPath(), "spawndev_jsliveswarm_" + Guid.NewGuid().ToString("N").Substring(0, 8));
Directory.CreateDirectory(tmpRoot);
Console.WriteLine($"C# client downloading to: {tmpRoot}");

// VerboseLogging left off by default - kept here in a commented-out line for when
// the harness needs to be re-diagnosed. The BinaryJsonSerializer TypeInfoResolver
// fix in SpawnDev.RTC 1.1.6-rc.1 is what flipped this test from "C# never registers
// with tracker under file-based dotnet run" to green. Flip both on if rerunning in
// a hostile environment.
// WebTorrentClient.VerboseLogging = true;
// SpawnDev.RTC.Signaling.TrackerSignalingClient.VerboseLogging = true;

await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    // Disable DHT / LSD / PEX - we want the tracker path specifically.
    // Trackers stay enabled so Discovery connects to ws://127.0.0.1:<port>/announce.
    // Override DefaultTrackers to empty - the default list includes
    // wss://hub.spawndev.com:44365/announce (Origin-gated, 403's us) and
    // wss://tracker.openwebtorrent.com (public, may add noise). For this
    // interop test we want the torrent's own announce list only.
    EnableTrackers = true,
    EnableDht = false,
    EnableLsd = false,
    EnableUtPex = false,
    DefaultTrackers = Array.Empty<string>(),
});
var addOpts = new AddTorrentOptions { Path = tmpRoot };

var torrentBytes = File.ReadAllBytes(torrentPath);
var torrent = client.Add(torrentBytes, addOpts);
Console.WriteLine($"C# client added torrent: infoHash={torrent.WireInfoHashHex}, pieces={torrent.PieceCount}");

// --- 4. Wait for download completion. Also periodically dump tracker /stats so
// we can see who's actually in the swarm room from the tracker's perspective. ---
using var statsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
var deadline2 = DateTime.UtcNow.AddSeconds(90);
double lastProgress = -1;
int ticks = 0;
while (DateTime.UtcNow < deadline2)
{
    if (torrent.Done) break;
    var p = torrent.Progress;
    if (Math.Abs(p - lastProgress) > 0.01 || ticks % 10 == 0)
    {
        Console.WriteLine($"  C# progress={p:P1} downloaded={torrent.Downloaded} bytes peers={torrent.NumPeers} wires={torrent.Wires.Count}");
        lastProgress = p;
        try
        {
            var stats = await statsHttp.GetStringAsync($"{trackerUrl}/stats");
            Console.WriteLine($"  [Tracker /stats] {stats}");
        }
        catch (Exception ex) { Console.WriteLine($"  [Tracker /stats] err: {ex.Message}"); }
    }
    await Task.Delay(500);
    ticks++;
}

if (!torrent.Done)
{
    Console.Error.WriteLine($"C# client timed out. progress={torrent.Progress:P2} downloaded={torrent.Downloaded}/{torrent.Length}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    try { tracker.Kill(entireProcessTree: true); } catch { }
    return 5;
}
Console.WriteLine($"C# client download complete: {torrent.Downloaded} bytes across {torrent.Wires.Count} wires.");

// --- 5. Verify ---
var tf = torrent.Files?.FirstOrDefault();
if (tf == null) { Console.Error.WriteLine("No files on torrent after download"); return 6; }
var downloadedBytes = await tf.ReadAsync(0, (int)tf.Length);
// Reuse the payload bytes read at step 1 - re-opening payload.bin here races with
// the JS seeder which still has the file open for seeding. (Don't kill the seeder
// first either - we want the verification to run against a clean in-memory copy.)
var originalBytes = payloadBytes;

if (originalBytes.Length != downloadedBytes.Length)
{
    Console.Error.WriteLine($"Length mismatch: original={originalBytes.Length}, downloaded={downloadedBytes.Length}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    try { tracker.Kill(entireProcessTree: true); } catch { }
    return 7;
}

var originalHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
var downloadedHash = Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant();
if (originalHash != downloadedHash)
{
    Console.Error.WriteLine($"Hash mismatch:\n  original  {originalHash}\n  downloaded {downloadedHash}");
    try { seeder.Kill(entireProcessTree: true); } catch { }
    try { tracker.Kill(entireProcessTree: true); } catch { }
    return 8;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  JS-WebTorrent LIVE-SWARM PASS");
Console.WriteLine($"  {downloadedBytes.Length} bytes SHA-256 byte-identical ({downloadedHash.Substring(0, 16)}...)");
Console.WriteLine($"  Transport: webtorrent@^2 (Node.js + @roamhq/wrtc) -> SpawnDev.WebTorrent C#");
Console.WriteLine($"  Tracker : SpawnDev.RTC.Server (WebSocket, {trackerWsUrl})");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

try { seeder.Kill(entireProcessTree: true); } catch { }
try { await seeder.WaitForExitAsync(); } catch { }
try { tracker.Kill(entireProcessTree: true); } catch { }
try { await tracker.WaitForExitAsync(); } catch { }
return 0;
