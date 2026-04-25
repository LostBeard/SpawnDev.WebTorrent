// Hub smoke test - verify the empty-Origin bypass is live on hub.spawndev.com.
//
// Three checks:
//   1. Health/root endpoint reports a version >= 3.1.6 (the release with the
//      empty-Origin bypass) - sanity.
//   2. Desktop ClientWebSocket (no Origin header) can complete the WSS upgrade -
//      this is the new behavior shipped in SpawnDev.RTC.Server 1.0.5 + RTC 1.1.6;
//      prior to redeploy the hub 403'd here.
//   3. A request WITH a hostile Origin header still gets 403 - the allowlist
//      still rejects browser clients from unaffiliated origins.
//
// Run: dotnet run hub_smoke.cs

using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;

const string HubBase = "https://hub.spawndev.com:44365";
const string AnnounceWss = "wss://hub.spawndev.com:44365/announce";

using var http = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
});

// --- 1. Sanity: hub root reports a version >= 3.1.6 (the release where the
//        empty-Origin bypass we're verifying actually landed). Anything from
//        3.1.6 forward is a valid hub for this smoke test. ---
Console.WriteLine($"[hub-smoke] GET {HubBase}/ ...");
var rootJson = await http.GetStringAsync(HubBase + "/");
using var rootDoc = JsonDocument.Parse(rootJson);
var version = rootDoc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
var allowlistEnabled = rootDoc.RootElement.TryGetProperty("originAllowlistEnabled", out var a) && a.GetBoolean();
Console.WriteLine($"  version={version}, originAllowlistEnabled={allowlistEnabled}");
if (string.IsNullOrEmpty(version) || !Version.TryParse(version, out var parsed) || parsed < new Version(3, 1, 6))
{
    Console.Error.WriteLine($"  FAIL: hub version {version} is older than 3.1.6 (the release with the empty-Origin bypass we're verifying)");
    return 1;
}

// --- 2. Empty-Origin desktop client should complete WSS upgrade. ---
Console.WriteLine($"[hub-smoke] WSS upgrade WITHOUT Origin header (desktop C# default) ...");
using (var ws = new ClientWebSocket())
{
    // Deliberately don't SetRequestHeader("Origin", ...).
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        await ws.ConnectAsync(new Uri(AnnounceWss), cts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FAIL: empty-Origin upgrade rejected: {ex.GetType().Name}: {ex.Message}");
        return 2;
    }
    if (ws.State != WebSocketState.Open)
    {
        Console.Error.WriteLine($"  FAIL: socket not open after connect (state={ws.State})");
        return 3;
    }
    Console.WriteLine($"  OK: socket Open (no Origin header sent)");
    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "smoke-done", default); } catch { }
}

// --- 3. Origin posture matches the hub's reported config. ---
//      hub.spawndev.com runs with the Origin allowlist DISABLED as of 2026-04-25
//      (the only real abuse gate is TrackerGated TURN; signaling is open by design,
//      matching the public-tracker convention). The allowlist code stays in
//      SpawnDev.RTC.Server for any deployment that wants browser-side gating -
//      it's just opt-in via env var instead of always-on. So this check adapts:
//        - allowlistEnabled = true  -> hostile Origin must 403 (legacy hub posture)
//        - allowlistEnabled = false -> hostile Origin must connect (current hub posture)
Console.WriteLine($"[hub-smoke] WSS upgrade WITH hostile Origin (Origin posture check) ...");
using (var ws = new ClientWebSocket())
{
    ws.Options.SetRequestHeader("Origin", "https://evil.example.org");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    bool threw = false;
    try
    {
        await ws.ConnectAsync(new Uri(AnnounceWss), cts.Token);
    }
    catch (WebSocketException) { threw = true; }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FAIL: unexpected error type: {ex.GetType().Name}: {ex.Message}");
        return 4;
    }

    if (allowlistEnabled)
    {
        if (!threw)
        {
            Console.Error.WriteLine($"  FAIL: allowlist enabled but hostile Origin connected anyway (state={ws.State})");
            return 5;
        }
        Console.WriteLine($"  OK: allowlist enabled, hostile Origin rejected as expected");
    }
    else
    {
        if (threw || ws.State != WebSocketState.Open)
        {
            Console.Error.WriteLine($"  FAIL: allowlist disabled but hostile Origin was rejected (threw={threw}, state={ws.State})");
            return 5;
        }
        Console.WriteLine($"  OK: allowlist disabled, hostile Origin accepted (TURN gate still requires tracker session)");
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "smoke-done", default); } catch { }
    }
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"  HUB SMOKE PASS - {HubBase}");
Console.WriteLine($"  Hub version : {version}");
Console.WriteLine($"  Allowlist   : {(allowlistEnabled ? "ENABLED, browser-only (empty Origin bypassed)" : "DISABLED (signaling open; TURN still tracker-gated)")}");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
return 0;
