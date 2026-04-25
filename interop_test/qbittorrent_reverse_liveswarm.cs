// qBittorrent REVERSE LIVE-SWARM interop test.
//
// Symmetric counterpart to qbittorrent_liveswarm.cs. There the qBittorrent
// client seeds and we leech; here our C# client seeds and qBittorrent leeches.
// Closes the last open audit item in PLAN-BEP52-External-Interop.md Step 4.
//
// Steps:
//   1. Authenticate to qBittorrent Web UI.
//   2. Read qBittorrent's save_path.
//   3. Make sure qBittorrent has NO existing payload.bin torrent (delete + cleanup).
//   4. Spin up SpawnDev.WebTorrent C# client as the seeder via SeedAsync(payload).
//   5. Start a TcpListenerService on 127.0.0.1:<free port> bound to that client.
//   6. Add the same .torrent file to qBittorrent in paused state (so it doesn't
//      try to do its own peer discovery first).
//   7. Resume the qBittorrent torrent and POST /api/v2/torrents/addPeers pointing
//      qBittorrent at 127.0.0.1:<our listener port>. qBittorrent dials in - our
//      listener accepts, peeks the BT handshake, routes to our seeded torrent,
//      hands the socket off as a TcpPeer in responder mode.
//   8. Wait for qBittorrent to reach progress >= 1.0 or timeout.
//   9. Read the file qBittorrent wrote, SHA-256-verify against the original payload.
//  10. Clean up qBittorrent state (delete the torrent + its data file).
//
// Pre-reqs:
//   1. qBittorrent running with Web UI enabled (Tools -> Preferences -> Web UI).
//   2. spawndev_hybrid.torrent + payload.bin already generated (run gen_qbittorrent_test.cs first).
//   3. QBT_HOST/QBT_PORT/QBT_USER/QBT_PASS env vars OR --host/--port/--user/--pass.
//
// Run: dotnet run qbittorrent_reverse_liveswarm.cs [--host localhost] [--port 8080] [--user admin] [--pass adminadmin]

#:project D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent.csproj

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using SpawnDev.WebTorrent;

var host = Env("QBT_HOST") ?? Arg("--host") ?? "localhost";
var port = Env("QBT_PORT") ?? Arg("--port") ?? "8080";
var user = Env("QBT_USER") ?? Arg("--user") ?? "admin";
var pass = Env("QBT_PASS") ?? Arg("--pass") ?? "adminadmin";

var baseUrl = $"http://{host}:{port}";
var scriptPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.EndsWith("qbittorrent_reverse_liveswarm.cs"));
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

// --- 2. Read save_path so we know where to look for qBT's downloaded copy ---
var prefsJson = JsonDocument.Parse(await http.GetStringAsync("/api/v2/app/preferences"));
var savePath = prefsJson.RootElement.GetProperty("save_path").GetString()!;
Console.WriteLine($"qBittorrent save_path={savePath}");

// --- 3. Cleanup any pre-existing payload.bin torrent in qBT (release file lock) ---
async Task DeletePayloadTorrentsAsync(bool deleteFiles)
{
    var existing = JsonDocument.Parse(await http.GetStringAsync("/api/v2/torrents/info")).RootElement;
    foreach (var t in existing.EnumerateArray())
    {
        var name = t.GetProperty("name").GetString() ?? "";
        if (!name.StartsWith("payload.bin")) continue;
        var hash = t.GetProperty("hash").GetString() ?? "";
        if (string.IsNullOrEmpty(hash)) continue;
        await http.PostAsync("/api/v2/torrents/delete",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hash, ["deleteFiles"] = deleteFiles ? "true" : "false" }));
    }
}
await DeletePayloadTorrentsAsync(deleteFiles: true);
await Task.Delay(1500);

// Also wipe any leftover payload.bin in save_path so qBittorrent will actually
// download (not just instant-recheck a stale file from a previous run).
var qbtTargetPath = Path.Combine(savePath, "payload.bin");
try { if (File.Exists(qbtTargetPath)) File.Delete(qbtTargetPath); } catch { }

// --- 4. Bring up the C# seeder + listener ---
Console.WriteLine("Starting SpawnDev.WebTorrent C# seeder ...");

await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    // Same posture as the forward test - localhost interop, no need to talk
    // to public trackers / DHT / LSD / PEX.
    EnableTrackers = false,
    EnableDht = false,
    EnableLsd = false,
    EnableUtPex = false,
    DefaultTrackers = Array.Empty<string>(),
    // Spin up the inbound TCP listener on a kernel-assigned ephemeral port
    // bound to loopback. The kernel-assigned port comes back via
    // client.TcpListener.LocalEndPoint.Port once EnsureTcpListenerAsync
    // completes. Loopback is fine here since qBittorrent and our test are
    // on the same machine.
    TcpListenPort = 0,
    TcpListenAddress = IPAddress.Loopback,
});

// EnsureTcpListenerAsync runs fire-and-forget from the constructor when
// TcpListenPort is set. Wait for it to complete so we can read back the
// actual kernel-assigned port. Calling Ensure again is idempotent.
await client.EnsureTcpListenerAsync(0, IPAddress.Loopback);
int listenPort = client.TcpListener!.LocalEndPoint.Port;

var payloadBytes = File.ReadAllBytes(payloadPath);
// SeedAsync wants the data + a name; we don't pass the .torrent file because
// SeedAsync builds its own metadata. The hashes will match because the test
// payload is deterministic. We then *replace* the metadata's announce list
// with empty (already done above via DefaultTrackers + EnableTrackers=false).
var seedTorrent = await client.SeedAsync("payload.bin", payloadBytes,
    new TorrentCreatorOptions
    {
        PieceLength = 65536,
        MetaVersion = 2,
        Hybrid = true,
    });

Console.WriteLine($"C# seeder: WireInfoHashHex={seedTorrent.WireInfoHashHex}, V2InfoHash={seedTorrent.V2InfoHash}, Done={seedTorrent.Done}, Length={seedTorrent.Length}");

// Sanity: this must match the .torrent we'll feed qBittorrent (so qBT will
// actually try to talk to our seeded copy and not some unrelated info_hash).
var fileBytes = File.ReadAllBytes(torrentPath);
var (_, refMeta) = TorrentCreator.CreateFromBytes("payload.bin", payloadBytes,
    new TorrentCreatorOptions { PieceLength = 65536, MetaVersion = 2, Hybrid = true });
if (!string.Equals(seedTorrent.WireInfoHashHex, refMeta.WireInfoHashHex, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Seeded torrent's wire info_hash {seedTorrent.WireInfoHashHex} doesn't match the on-disk .torrent's info_hash {refMeta.WireInfoHashHex}. gen_qbittorrent_test.cs may need to be re-run.");
    return 4;
}

// The listener was started by the WebTorrentClient constructor (TcpListenPort
// option). Wire its log channel here so we still see accept/reject events.
client.TcpListener.OnLog += msg => Console.WriteLine(msg);
var listener = client.TcpListener;  // alias used below; client owns lifetime
Console.WriteLine($"C# listener on 127.0.0.1:{listenPort}");

// --- 5. Add the .torrent to qBittorrent paused, then resume + addPeers ---
using (var multi = new MultipartFormDataContent())
{
    var torrentFileBytes = File.ReadAllBytes(torrentPath);
    var fileContent = new ByteArrayContent(torrentFileBytes);
    fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-bittorrent");
    multi.Add(fileContent, "torrents", "spawndev_hybrid.torrent");
    multi.Add(new StringContent(savePath), "savepath");
    multi.Add(new StringContent("false"), "skip_checking");
    multi.Add(new StringContent("true"), "paused"); // start paused so we can register peers first
    var addResp = await http.PostAsync("/api/v2/torrents/add", multi);
    if (!addResp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"qBittorrent add failed: {addResp.StatusCode}");
        return 5;
    }
}
await Task.Delay(500);

// Find the torrent we just added.
var ourJson = JsonDocument.Parse(await http.GetStringAsync("/api/v2/torrents/info"));
var ours = ourJson.RootElement.EnumerateArray().FirstOrDefault(t => (t.GetProperty("name").GetString() ?? "").StartsWith("payload.bin"));
if (ours.ValueKind == JsonValueKind.Undefined) { Console.Error.WriteLine("Add failed to register torrent."); return 6; }
var qbtHash = ours.GetProperty("hash").GetString()!;
Console.WriteLine($"qBittorrent torrent registered (hash={qbtHash}, paused).");

// Resume the torrent so qBT actually starts asking peers for pieces.
await http.PostAsync("/api/v2/torrents/resume", new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = qbtHash }));
await Task.Delay(500);

// Tell qBT about our listener as a peer for this torrent.
async Task<bool> AddPeerAsync()
{
    var addPeersResp = await http.PostAsync("/api/v2/torrents/addPeers",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"] = qbtHash,
            ["peers"] = $"127.0.0.1:{listenPort}",
        }));
    return addPeersResp.IsSuccessStatusCode;
}
if (!await AddPeerAsync())
{
    Console.Error.WriteLine("addPeers call failed. qBittorrent < 4.4 doesn't have this endpoint.");
    return 7;
}
Console.WriteLine($"Pointed qBittorrent at 127.0.0.1:{listenPort} as a peer for {qbtHash[..16]}...");

// --- 6. Wait for qBittorrent to download to completion ---
var deadline = DateTime.UtcNow.AddSeconds(60);
double lastProgress = -1;
int reAddTicks = 0;
while (DateTime.UtcNow < deadline)
{
    var info = JsonDocument.Parse(await http.GetStringAsync($"/api/v2/torrents/info?hashes={qbtHash}"));
    var entry = info.RootElement.EnumerateArray().FirstOrDefault();
    if (entry.ValueKind != JsonValueKind.Undefined)
    {
        var progress = entry.GetProperty("progress").GetDouble();
        var state = entry.GetProperty("state").GetString() ?? "";
        var dl = entry.GetProperty("downloaded").GetInt64();
        var numPeers = entry.TryGetProperty("num_peers", out var npProp) ? npProp.GetInt32() : 0;
        var numLeechs = entry.TryGetProperty("num_leechs", out var nlProp) ? nlProp.GetInt32() : 0;

        if (Math.Abs(progress - lastProgress) > 0.01 || (DateTime.UtcNow - deadline).TotalSeconds % 5 < 1)
        {
            Console.WriteLine($"  qBT progress={progress:P1} state={state} downloaded={dl} peers={numPeers} leechs={numLeechs} listenerAccepted={listener.AcceptedCount}");
            lastProgress = progress;
        }
        if (progress >= 1.0)
        {
            Console.WriteLine($"qBittorrent reached 100% (state={state}).");
            break;
        }
    }
    // Re-prod qBT every ~5s in case the first add didn't take, until we see a connection.
    if (++reAddTicks % 10 == 0 && listener.AcceptedCount == 0)
        await AddPeerAsync();
    await Task.Delay(500);
}

// --- 7. Verify ---
// Re-read the qBT-side state once more to confirm 100%.
var finalInfo = JsonDocument.Parse(await http.GetStringAsync($"/api/v2/torrents/info?hashes={qbtHash}")).RootElement.EnumerateArray().FirstOrDefault();
double finalProgress = finalInfo.ValueKind != JsonValueKind.Undefined ? finalInfo.GetProperty("progress").GetDouble() : 0.0;
if (finalProgress < 1.0)
{
    Console.Error.WriteLine($"qBittorrent never reached 100% within deadline. Final progress={finalProgress:P2}, listenerAccepted={listener.AcceptedCount}, listenerRejected={listener.RejectedCount}");
    await DeletePayloadTorrentsAsync(deleteFiles: true);
    // listener disposal handled by client (await using)
    return 8;
}

// Read the file qBittorrent wrote and verify byte-identity.
// qBT may briefly hold the file open after marking complete - retry a few times.
byte[]? downloadedBytes = null;
for (int i = 0; i < 10; i++)
{
    try
    {
        downloadedBytes = File.ReadAllBytes(qbtTargetPath);
        break;
    }
    catch (IOException) { await Task.Delay(500); }
}
if (downloadedBytes == null)
{
    Console.Error.WriteLine($"Failed to read {qbtTargetPath} (file locked).");
    await DeletePayloadTorrentsAsync(deleteFiles: true);
    // listener disposal handled by client (await using)
    return 9;
}

if (downloadedBytes.Length != payloadBytes.Length)
{
    Console.Error.WriteLine($"Length mismatch: original={payloadBytes.Length}, qBT wrote {downloadedBytes.Length}");
    await DeletePayloadTorrentsAsync(deleteFiles: true);
    // listener disposal handled by client (await using)
    return 10;
}

var originalHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
var downloadedHash = Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant();
if (originalHash != downloadedHash)
{
    Console.Error.WriteLine($"Hash mismatch:\n  original   {originalHash}\n  downloaded {downloadedHash}");
    await DeletePayloadTorrentsAsync(deleteFiles: true);
    // listener disposal handled by client (await using)
    return 11;
}

Console.WriteLine();
Console.WriteLine("===============================================================");
Console.WriteLine("  qBittorrent REVERSE LIVE-SWARM PASS");
Console.WriteLine($"  {downloadedBytes.Length} bytes SHA-256 byte-identical ({downloadedHash[..16]}...)");
Console.WriteLine($"  Direction : SpawnDev.WebTorrent C# (seed) -> qBittorrent (leech)");
Console.WriteLine($"  Transport : TCP peer-wire on 127.0.0.1:{listenPort} (TcpListenerService)");
Console.WriteLine($"  Listener  : accepted={listener.AcceptedCount}, rejected={listener.RejectedCount}");
Console.WriteLine("===============================================================");

await DeletePayloadTorrentsAsync(deleteFiles: true);
// listener disposal handled by client (await using)
return 0;

static string? Env(string k) => Environment.GetEnvironmentVariable(k);
static string? Arg(string flag)
{
    var args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}
