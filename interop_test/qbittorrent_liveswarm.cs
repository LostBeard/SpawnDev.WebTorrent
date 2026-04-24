// qBittorrent LIVE-SWARM interop test.
//
// Goes beyond the static hash-match-and-recheck in `qbittorrent_interop.cs`:
// actually transfers bytes over the wire between qBittorrent and our C# client.
//
// Steps:
//   1. Authenticate to qBittorrent Web UI (same driver pattern as qbittorrent_interop.cs).
//   2. Read qBittorrent's configured listen_port so we know where its BitTorrent TCP
//      socket is accepting connections.
//   3. Ensure qBittorrent has spawndev_hybrid.torrent fully seeded (payload.bin copied
//      into save_path, add torrent, force-recheck, wait for 100%). Reuses the exact
//      pattern from qbittorrent_interop.cs.
//   4. Spin up a SpawnDev.WebTorrent client with NO payload data and add the same
//      torrent from disk.
//   5. Manually construct a TcpPeer and ConnectAsync to 127.0.0.1:<listen_port>.
//      Inject via Torrent.AddPeer to bypass tracker discovery (both clients share a
//      WSS tracker URL that qBittorrent can't use and we don't need for localhost).
//   6. Wait for torrent.Progress == 1.0 or timeout.
//   7. Hash the downloaded bytes byte-for-byte against the original payload.bin.
//   8. Report PASS/FAIL.
//
// This is the "Step 4" of PLAN-BEP52-External-Interop.md - live-swarm bi-directional
// active seeding - from the C# side: qBittorrent seeds, we download.
//
// Pre-reqs:
//   1. Running qBittorrent with Web UI enabled (Tools → Preferences → Web UI).
//   2. spawndev_hybrid.torrent + payload.bin already generated (run gen_qbittorrent_test.cs first).
//   3. QBT_HOST/QBT_PORT/QBT_USER/QBT_PASS env vars, or --host/--port/--user/--pass.
//
// Run: dotnet run qbittorrent_liveswarm.cs [--host localhost] [--port 8080] [--user admin] [--pass adminadmin]

#:project D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent.csproj

using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SpawnDev.WebTorrent;

var host = Env("QBT_HOST") ?? Arg("--host") ?? "localhost";
var port = Env("QBT_PORT") ?? Arg("--port") ?? "8080";
var user = Env("QBT_USER") ?? Arg("--user") ?? "admin";
var pass = Env("QBT_PASS") ?? Arg("--pass") ?? "adminadmin";

var baseUrl = $"http://{host}:{port}";
var scriptPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.EndsWith("qbittorrent_liveswarm.cs"));
var scriptDir = scriptPath != null ? Path.GetDirectoryName(Path.GetFullPath(scriptPath))! : Directory.GetCurrentDirectory();
var outDir = Path.Combine(scriptDir, "output");

var torrentPath = Path.Combine(outDir, "spawndev_hybrid.torrent");
var payloadPath = Path.Combine(outDir, "payload.bin");
if (!File.Exists(torrentPath)) { Console.Error.WriteLine($"Missing {torrentPath}. Run gen_qbittorrent_test.cs first."); return 2; }
if (!File.Exists(payloadPath)) { Console.Error.WriteLine($"Missing {payloadPath}. Run gen_qbittorrent_test.cs first."); return 2; }

using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new() };
using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Referrer = new Uri(baseUrl);

Console.WriteLine($"Connecting to qBittorrent Web UI at {baseUrl} ...");

// --- 1. Authenticate ---
var loginForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = pass });
var loginResp = await http.PostAsync("/api/v2/auth/login", loginForm);
if (!loginResp.IsSuccessStatusCode || (await loginResp.Content.ReadAsStringAsync()).Trim() != "Ok.")
{
    Console.Error.WriteLine("Login failed. Check Web UI is enabled and credentials match.");
    return 3;
}

// --- 2. Read preferences (listen_port + save_path) ---
var prefsJson = JsonDocument.Parse(await http.GetStringAsync("/api/v2/app/preferences"));
var listenPort = prefsJson.RootElement.GetProperty("listen_port").GetInt32();
var savePath = prefsJson.RootElement.GetProperty("save_path").GetString()!;
Console.WriteLine($"qBittorrent listen_port={listenPort}, save_path={savePath}");

// --- 3. Ensure qBittorrent is seeding spawndev_hybrid.torrent ---
// STEP 3a: Clear any stale payload.bin torrents FIRST so qBittorrent releases its
// file handle - otherwise the File.Copy below hits "user-mapped section open"
// from a previous run's seeder still holding the file mapped for upload.
var existing = JsonDocument.Parse(await http.GetStringAsync("/api/v2/torrents/info")).RootElement;
foreach (var t in existing.EnumerateArray())
{
    var name = t.GetProperty("name").GetString() ?? "";
    if (!name.StartsWith("payload.bin")) continue;
    var hash = t.GetProperty("hash").GetString() ?? "";
    if (string.IsNullOrEmpty(hash)) continue;
    await http.PostAsync("/api/v2/torrents/delete",
        new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hash, ["deleteFiles"] = "false" }));
}
await Task.Delay(1500); // give qBittorrent time to release the memory-mapped file

// STEP 3b: Copy payload into save_path so recheck can verify.
var saveFile = Path.Combine(savePath, "payload.bin");
File.Copy(payloadPath, saveFile, overwrite: true);
Console.WriteLine($"Copied payload.bin -> {saveFile}");

// Re-add (hybrid; has both v1 + v2).
using (var multi = new MultipartFormDataContent())
{
    var fileBytes = File.ReadAllBytes(torrentPath);
    var fileContent = new ByteArrayContent(fileBytes);
    fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-bittorrent");
    multi.Add(fileContent, "torrents", "spawndev_hybrid.torrent");
    multi.Add(new StringContent(savePath), "savepath");
    multi.Add(new StringContent("true"), "skip_checking"); // we'll force-recheck next
    multi.Add(new StringContent("false"), "paused"); // we want it seeding
    await http.PostAsync("/api/v2/torrents/add", multi);
}
await Task.Delay(500);

// Find it + force recheck.
var ourJson = JsonDocument.Parse(await http.GetStringAsync("/api/v2/torrents/info"));
var ours = ourJson.RootElement.EnumerateArray().FirstOrDefault(t => (t.GetProperty("name").GetString() ?? "").StartsWith("payload.bin"));
if (ours.ValueKind == JsonValueKind.Undefined) { Console.Error.WriteLine("Add failed to register torrent."); return 4; }
var hashForApi = ours.GetProperty("hash").GetString()!;

await http.PostAsync("/api/v2/torrents/recheck",
    new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hashForApi }));
await Task.Delay(500);

// Wait for qBittorrent to complete rehash + reach seeding state.
var seedReady = false;
for (int i = 0; i < 60; i++) // up to 30s
{
    var info = JsonDocument.Parse(await http.GetStringAsync($"/api/v2/torrents/info?hashes={hashForApi}"));
    var entry = info.RootElement.EnumerateArray().FirstOrDefault();
    if (entry.ValueKind != JsonValueKind.Undefined)
    {
        var progress = entry.GetProperty("progress").GetDouble();
        var state = entry.GetProperty("state").GetString() ?? "";
        if (progress >= 1.0 && (state.Contains("UP") || state == "seeding" || state == "stalledUP" || state == "queuedUP" || state == "uploading"))
        {
            seedReady = true;
            break;
        }
    }
    await Task.Delay(500);
}
if (!seedReady) { Console.Error.WriteLine("qBittorrent never reached seeding state on hybrid torrent."); return 5; }
Console.WriteLine($"qBittorrent is seeding (hash={hashForApi}).");

// --- 4. SpawnDev.WebTorrent client in a temp dir, add the torrent with no data ---
var tmpRoot = Path.Combine(Path.GetTempPath(), "spawndev_liveswarm_" + Guid.NewGuid().ToString("N").Substring(0, 8));
Directory.CreateDirectory(tmpRoot);
Console.WriteLine($"C# client downloading to: {tmpRoot}");

await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    // Disable trackers for this test (our WSS trackers don't help for qBittorrent interop,
    // and leaving them on risks the client announcing to hub.spawndev.com unnecessarily).
    EnableTrackers = false,
    EnableDht = false,
    EnableLsd = false,
    EnableUtPex = false,
});
var addOpts = new AddTorrentOptions { Path = tmpRoot };

var torrentBytes = File.ReadAllBytes(torrentPath);
var torrent = client.Add(torrentBytes, addOpts);
Console.WriteLine($"C# client added torrent: infoHash={torrent.WireInfoHashHex}, pieces={torrent.PieceCount}");

// --- 5. Inject qBittorrent as a direct peer (bypassing tracker discovery) ---
// Retry the TCP connect up to 5 times - qBittorrent can briefly drop its
// incoming-TCP-listener during the add+recheck cycle, and fresh cold connects
// can fail transiently on Windows for a beat.
Console.WriteLine($"Connecting to 127.0.0.1:{listenPort} (qBittorrent's TCP listen) ...");
TcpPeer? tcpPeer = null;
for (int attempt = 1; attempt <= 5; attempt++)
{
    tcpPeer = new TcpPeer(initiator: true);
    await tcpPeer.ConnectAsync($"127.0.0.1:{listenPort}");
    if (tcpPeer.Connected) break;
    await tcpPeer.DisposeAsync();
    tcpPeer = null;
    Console.WriteLine($"  TCP connect attempt {attempt} failed, retrying in 1s...");
    await Task.Delay(1000);
}
if (tcpPeer is null || !tcpPeer.Connected)
{
    Console.Error.WriteLine($"TCP connect to 127.0.0.1:{listenPort} failed after 5 attempts.");
    return 6;
}
torrent.AddPeer(tcpPeer);
Console.WriteLine($"Peer added, TCP handshake pending.");

// --- 6. Wait for download completion ---
var deadline = DateTime.UtcNow.AddSeconds(120);
double lastProgress = -1;
while (DateTime.UtcNow < deadline)
{
    var p = torrent.Progress;
    if (p >= 1.0 && torrent.Done) break;
    if (Math.Abs(p - lastProgress) > 0.01)
    {
        Console.WriteLine($"  progress={p:P1} downloaded={torrent.Downloaded} bytes peers={torrent.NumPeers} wires={torrent.Wires.Count}");
        lastProgress = p;
    }
    await Task.Delay(500);
}

if (!torrent.Done)
{
    Console.Error.WriteLine($"Timed out waiting for download completion. Final progress={torrent.Progress:P2}, downloaded={torrent.Downloaded} of {torrent.Length}.");
    return 7;
}
Console.WriteLine($"Download complete in {torrent.Downloaded} bytes across {torrent.Wires.Count} wires.");

// --- 7. Verify byte-for-byte match with original ---
// Client is using MemoryChunkStore (no AsyncFileSystem configured for desktop console
// scripts), so pull the bytes through the torrent's in-memory read API rather than
// looking for a file on disk.
var tf = torrent.Files?.FirstOrDefault();
if (tf == null) { Console.Error.WriteLine("No files on torrent after download"); return 8; }
var downloadedBytes = await tf.ReadAsync(0, (int)tf.Length);

var originalBytes = File.ReadAllBytes(payloadPath);

if (originalBytes.Length != downloadedBytes.Length)
{
    Console.Error.WriteLine($"Length mismatch: original={originalBytes.Length}, downloaded={downloadedBytes.Length}");
    return 9;
}

var originalHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
var downloadedHash = Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant();
if (originalHash != downloadedHash)
{
    Console.Error.WriteLine($"Hash mismatch: original SHA-256={originalHash}, downloaded={downloadedHash}");
    return 10;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  LIVE-SWARM PASS: qBittorrent seeded, SpawnDev.WebTorrent downloaded,");
Console.WriteLine($"  {downloadedBytes.Length} bytes byte-identical (SHA-256 {downloadedHash.Substring(0, 16)}...)");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

return 0;

static string? Env(string key) => Environment.GetEnvironmentVariable(key);
static string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
