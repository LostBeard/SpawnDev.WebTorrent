// qBittorrent Web UI interop driver.
// Adds our three SpawnDev-generated torrents (v1, pure-v2, hybrid) to a running
// qBittorrent instance via its Web UI REST API, drops payload.bin into qBittorrent's
// save dir, forces a recheck, and verifies 100% piece completion on all three.
//
// This is the BEP 52 external-interop manual step automated.
//
// Pre-reqs:
//   1. qBittorrent running with Web UI enabled (Tools → Preferences → Web UI).
//   2. Test torrents + payload.bin already generated (see gen_qbittorrent_test.cs).
//   3. QBT_HOST / QBT_PORT / QBT_USER / QBT_PASS env vars, or pass via --host/--port/--user/--pass.
//
// Run: dotnet run qbittorrent_interop.cs [--host localhost] [--port 8080] [--user admin] [--pass adminadmin]

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

var host = Env("QBT_HOST") ?? Arg("--host") ?? "localhost";
var port = Env("QBT_PORT") ?? Arg("--port") ?? "8080";
var user = Env("QBT_USER") ?? Arg("--user") ?? "admin";
var pass = Env("QBT_PASS") ?? Arg("--pass") ?? "adminadmin";

var baseUrl = $"http://{host}:{port}";
var scriptDir = Directory.GetCurrentDirectory();
var scriptPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.EndsWith("qbittorrent_interop.cs"));
if (scriptPath != null) scriptDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;

var outDir = Path.Combine(scriptDir, "output");
var payloadPath = Path.Combine(outDir, "payload.bin");
var torrents = new[]
{
    ("spawndev_v1",     Path.Combine(outDir, "spawndev_v1.torrent"),     "v1-only  (SHA-1)",         Expected.V1),
    ("spawndev_v2",     Path.Combine(outDir, "spawndev_v2.torrent"),     "pure v2  (SHA-256 Merkle)",Expected.V2),
    ("spawndev_hybrid", Path.Combine(outDir, "spawndev_hybrid.torrent"), "hybrid v1+v2",             Expected.Hybrid),
};

// Hashes printed by gen_qbittorrent_test.cs for the deterministic seed.
// Update these if you regenerate with a different seed.
var expectedHashes = new Dictionary<string, (string v1, string v2)>
{
    ["spawndev_v1"]     = ("ddaf02162c94d64de2124199027d5c2c4f75437d", ""),
    ["spawndev_v2"]     = ("", "74f9a7f593f8f27460347f7479d6b4939a7c21a5f03e700c5ce2b5ee65ba75ef"),
    ["spawndev_hybrid"] = ("c5abd097f4c9181d9d2676a24c1bb4e90dc5cfae", "208c798d1e7e30a2e9014b2c1ca26f9f422793fc7fc8fed19cb94cf6cd0b895c"),
};

foreach (var (_, path, _, _) in torrents)
    if (!File.Exists(path)) { Console.Error.WriteLine($"Missing {path}. Run gen_qbittorrent_test.cs first."); return 2; }
if (!File.Exists(payloadPath)) { Console.Error.WriteLine($"Missing {payloadPath}. Run gen_qbittorrent_test.cs first."); return 2; }

using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new() };
using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Referrer = new Uri(baseUrl);

Console.WriteLine($"Connecting to qBittorrent Web UI at {baseUrl} ...");

// 1. Authenticate.
var loginForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = pass });
var loginResp = await http.PostAsync("/api/v2/auth/login", loginForm);
var loginBody = await loginResp.Content.ReadAsStringAsync();
if (!loginResp.IsSuccessStatusCode || loginBody.Trim() != "Ok.")
{
    Console.Error.WriteLine($"Login failed: HTTP {(int)loginResp.StatusCode} body='{loginBody}'");
    Console.Error.WriteLine("Check Tools → Preferences → Web UI is enabled and the credentials are correct.");
    return 3;
}
Console.WriteLine("Login OK.");

// 1b. Detect libtorrent version. libtorrent 1.2 can't parse BEP 52 v2 info dicts
// (rejects pure-v2 with HTTP 415; loads hybrid as v1-only, infohash_v2 empty).
// libtorrent 2.0+ handles both v2 and hybrid end-to-end.
var buildInfoResp = await http.GetAsync("/api/v2/app/buildInfo");
var buildJson = JsonDocument.Parse(await buildInfoResp.Content.ReadAsStringAsync());
var libtorrentVersion = buildJson.RootElement.GetProperty("libtorrent").GetString() ?? "";
var qbtVersionResp = await http.GetAsync("/api/v2/app/version");
var qbtVersion = (await qbtVersionResp.Content.ReadAsStringAsync()).Trim();
var libtorrentMajor = libtorrentVersion.Split('.').FirstOrDefault() ?? "?";
bool bep52Capable = libtorrentMajor is "2" or "3";
Console.WriteLine($"qBittorrent {qbtVersion} / libtorrent {libtorrentVersion} / BEP 52 v2 capable: {(bep52Capable ? "YES" : "NO (libtorrent 1.2 is v1-only)")}");

// 2. Get qBittorrent's configured save path.
var prefsResp = await http.GetAsync("/api/v2/app/preferences");
var prefsJson = JsonDocument.Parse(await prefsResp.Content.ReadAsStringAsync());
var savePath = prefsJson.RootElement.GetProperty("save_path").GetString()!;
Console.WriteLine($"qBittorrent save_path: {savePath}");

// 3. Clean slate: remove any leftover payload.bin torrents from a previous run so we
// don't collide on re-add. Keeps the data file on disk (deleteFiles=false).
var existingResp = await http.GetAsync("/api/v2/torrents/info");
var existingJson = JsonDocument.Parse(await existingResp.Content.ReadAsStringAsync());
var stale = existingJson.RootElement.EnumerateArray()
    .Where(t => (t.GetProperty("name").GetString() ?? "").StartsWith("payload.bin"))
    .Select(t => t.GetProperty("hash").GetString() ?? "")
    .Where(h => !string.IsNullOrEmpty(h))
    .ToArray();
if (stale.Length > 0)
{
    // Delete one-at-a-time: FormUrlEncodedContent URL-encodes the `|` separator to
    // %7C and qBittorrent's parser then only sees the first hash. Per-hash POSTs
    // side-step the encoding. Also 500ms settle between to let qBittorrent finalize.
    foreach (var h in stale)
    {
        var delForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"] = h,
            ["deleteFiles"] = "false",
        });
        await http.PostAsync("/api/v2/torrents/delete", delForm);
    }
    Console.WriteLine($"Cleaned {stale.Length} leftover payload.bin torrents from previous runs");
    await Task.Delay(1000); // let qBittorrent finish tearing them down
}

// 4. Copy payload.bin into save path so qBittorrent finds it on recheck.
var savePayload = Path.Combine(savePath, "payload.bin");
File.Copy(payloadPath, savePayload, overwrite: true);
Console.WriteLine($"Copied payload.bin → {savePayload} ({new FileInfo(savePayload).Length} bytes)");

// 4. Add each torrent, recheck, poll for completion.
var report = new List<string>();
var allPass = true;

foreach (var (name, path, desc, exp) in torrents)
{
    Console.WriteLine($"\n── {name}.torrent ({desc}) ──");

    // Pure-v2 can only be interop-tested against libtorrent 2.0+. On 1.2, skip with
    // a clear message (not a FAIL — infrastructure limit, not our bug).
    if (exp == Expected.V2 && !bep52Capable)
    {
        Console.WriteLine($"  SKIP: pure-v2 requires libtorrent 2.0+; this instance runs {libtorrentVersion}");
        report.Add($"{name}: SKIP (needs libtorrent 2.0; running {libtorrentVersion})");
        continue;
    }

    using var multi = new MultipartFormDataContent();
    var fileBytes = File.ReadAllBytes(path);
    var fileContent = new ByteArrayContent(fileBytes);
    fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-bittorrent");
    multi.Add(fileContent, "torrents", Path.GetFileName(path));
    multi.Add(new StringContent(savePath), "savepath");
    multi.Add(new StringContent("true"), "skip_checking"); // we'll force-recheck explicitly
    multi.Add(new StringContent("true"), "paused");

    var addResp = await http.PostAsync("/api/v2/torrents/add", multi);
    if (!addResp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"  add failed: HTTP {(int)addResp.StatusCode}");
        report.Add($"{name}: ADD FAIL");
        allPass = false;
        continue;
    }
    Console.WriteLine($"  add OK");

    await Task.Delay(500); // give qBittorrent a moment to register

    // 5. Fetch torrent list and find ours by name.
    var listResp = await http.GetAsync("/api/v2/torrents/info");
    var listJson = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
    var ours = listJson.RootElement.EnumerateArray()
        .FirstOrDefault(t => t.GetProperty("name").GetString()!.StartsWith("payload.bin"));
    if (ours.ValueKind == JsonValueKind.Undefined)
    {
        Console.Error.WriteLine("  torrent not found in /api/v2/torrents/info after add");
        report.Add($"{name}: NOT FOUND");
        allPass = false;
        continue;
    }

    // qBittorrent 4.4+ reports both hashes; older builds only report hash.
    var v1Reported = ours.TryGetProperty("infohash_v1", out var h1) ? h1.GetString() ?? "" : ours.GetProperty("hash").GetString() ?? "";
    var v2Reported = ours.TryGetProperty("infohash_v2", out var h2) ? h2.GetString() ?? "" : "";
    var hashForApi = !string.IsNullOrEmpty(v1Reported) ? v1Reported : v2Reported;
    Console.WriteLine($"  hash (API key)   = {hashForApi}");
    Console.WriteLine($"  infohash_v1      = {(string.IsNullOrEmpty(v1Reported) ? "(none)" : v1Reported)}");
    Console.WriteLine($"  infohash_v2      = {(string.IsNullOrEmpty(v2Reported) ? "(none)" : v2Reported)}");

    // 6. Compare against our expected hashes.
    // libtorrent 1.2 never reports infohash_v2 even for hybrid torrents; it parses
    // only the v1 view. Treat empty v2 as "not reported" rather than mismatch when
    // the host isn't BEP 52 v2 capable.
    var (expV1, expV2) = expectedHashes[name];
    var hashMatch = true;
    if (!string.IsNullOrEmpty(expV1) && !v1Reported.Equals(expV1, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"  v1 MISMATCH: expected {expV1}, got {v1Reported}");
        hashMatch = false;
    }
    if (!string.IsNullOrEmpty(expV2))
    {
        if (!bep52Capable && string.IsNullOrEmpty(v2Reported))
            Console.WriteLine($"  v2 not reported (libtorrent 1.2 v1-only parse — expected on this host)");
        else if (!v2Reported.Equals(expV2, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"  v2 MISMATCH: expected {expV2}, got {v2Reported}");
            hashMatch = false;
        }
    }

    if (!hashMatch)
    {
        report.Add($"{name}: HASH MISMATCH");
        allPass = false;
    }
    else
    {
        Console.WriteLine($"  hash check: OK (matches SpawnDev-generated hashes)");
    }

    // 7. Force recheck.
    var recheckForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hashForApi });
    var recheckResp = await http.PostAsync("/api/v2/torrents/recheck", recheckForm);
    if (!recheckResp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"  recheck start failed: HTTP {(int)recheckResp.StatusCode}");
        report.Add($"{name}: RECHECK FAIL");
        allPass = false;
        continue;
    }
    Console.WriteLine($"  recheck started, polling ...");

    // 8. Poll for completion. Find our torrent in the unfiltered list each tick
    // (filtering by hashes= sometimes returns empty for pure-v2 entries depending
    // on qBittorrent version - unfiltered is bulletproof). Success condition is
    // simply progress >= 1.0; we report the state for context but don't gate on it.
    var deadline = DateTime.UtcNow.AddSeconds(60);
    bool done = false;
    double progress = 0;
    string state = "?";
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(1000);
        var pollResp = await http.GetAsync("/api/v2/torrents/info");
        var pollJson = JsonDocument.Parse(await pollResp.Content.ReadAsStringAsync());
        var match = pollJson.RootElement.EnumerateArray().FirstOrDefault(t =>
        {
            var h1 = t.TryGetProperty("infohash_v1", out var p1) ? (p1.GetString() ?? "") : (t.GetProperty("hash").GetString() ?? "");
            var h2 = t.TryGetProperty("infohash_v2", out var p2) ? (p2.GetString() ?? "") : "";
            return h1.Equals(hashForApi, StringComparison.OrdinalIgnoreCase)
                || h2.Equals(hashForApi, StringComparison.OrdinalIgnoreCase)
                || (t.GetProperty("hash").GetString() ?? "").Equals(hashForApi, StringComparison.OrdinalIgnoreCase);
        });
        if (match.ValueKind == JsonValueKind.Undefined) continue;
        progress = match.GetProperty("progress").GetDouble();
        state = match.GetProperty("state").GetString()!;
        Console.Write($"\r  recheck: {progress * 100:F1}% state={state}      ");
        if (progress >= 1.0)
        {
            done = true;
            Console.WriteLine();
            break;
        }
    }

    if (!done)
    {
        Console.Error.WriteLine($"\n  recheck did NOT reach 100% in 45s (last progress={progress * 100:F1}%, state={state})");
        if (hashMatch) // avoid double-report if already flagged
            report.Add($"{name}: PIECES INCOMPLETE ({progress*100:F1}%)");
        allPass = false;
    }
    else
    {
        Console.WriteLine($"  piece verification: 100% ({progress*100:F1}% state={state})");
        if (hashMatch)
            report.Add($"{name}: PASS ({desc})");
    }

    // 9. Remove the torrent so a re-run doesn't hit dupes. Keep data on disk (deleteFiles=false).
    var removeForm = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["hashes"] = hashForApi,
        ["deleteFiles"] = "false",
    });
    await http.PostAsync("/api/v2/torrents/delete", removeForm);
}

// Final cleanup sweep: some qBittorrent versions don't remove v2/hybrid torrents reliably
// when called with the hash key we used for add — re-fetch the list by name and nuke
// anything named payload.bin that survived the per-iteration deletes.
await Task.Delay(500);
var finalListResp = await http.GetAsync("/api/v2/torrents/info");
var finalList = JsonDocument.Parse(await finalListResp.Content.ReadAsStringAsync());
var orphans = finalList.RootElement.EnumerateArray()
    .Where(t => (t.GetProperty("name").GetString() ?? "").StartsWith("payload.bin"))
    .Select(t => t.GetProperty("hash").GetString() ?? "")
    .Where(h => !string.IsNullOrEmpty(h))
    .ToArray();
if (orphans.Length > 0)
{
    foreach (var h in orphans)
    {
        var delForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"] = h,
            ["deleteFiles"] = "false",
        });
        await http.PostAsync("/api/v2/torrents/delete", delForm);
    }
    await Task.Delay(500);
    Console.WriteLine($"Final cleanup: removed {orphans.Length} orphan payload.bin entries (test data retained on disk)");
}

Console.WriteLine("\n══ Summary ══");
foreach (var line in report) Console.WriteLine($"  {line}");
Console.WriteLine();
var hasSkip = report.Any(r => r.Contains("SKIP"));
if (allPass && !hasSkip)
{
    Console.WriteLine("✓ qBittorrent interop: ALL PASS (v1 + pure-v2 + hybrid all hash-match and piece-verify clean)");
    return 0;
}
if (allPass && hasSkip)
{
    Console.WriteLine("◐ qBittorrent interop: PARTIAL (every exercised flavor passed; some skipped for capability reasons - see report above)");
    Console.WriteLine($"  Host BEP 52 v2 capable: {(bep52Capable ? "YES" : "NO (libtorrent " + libtorrentVersion + " — need 2.0+ build for pure-v2 interop)")}");
    return 0;
}
Console.WriteLine("✗ qBittorrent interop: FAILURES above");
return 1;

static string? Env(string name) => Environment.GetEnvironmentVariable(name);
static string? Arg(string flag)
{
    var args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

enum Expected { V1, V2, Hybrid }
