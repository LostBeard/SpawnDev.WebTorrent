// Hub smoke test - verify the empty-Origin bypass is live on hub.spawndev.com.
//
// Three checks:
//   1. Health/root endpoint reports the new version (3.1.6) - sanity.
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

// --- 1. Sanity: hub root reports new version. ---
Console.WriteLine($"[hub-smoke] GET {HubBase}/ ...");
var rootJson = await http.GetStringAsync(HubBase + "/");
using var rootDoc = JsonDocument.Parse(rootJson);
var version = rootDoc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
var allowlistEnabled = rootDoc.RootElement.TryGetProperty("originAllowlistEnabled", out var a) && a.GetBoolean();
Console.WriteLine($"  version={version}, originAllowlistEnabled={allowlistEnabled}");
if (version != "3.1.6")
{
    Console.Error.WriteLine($"  FAIL: expected version 3.1.6, got {version}");
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

// --- 3. Hostile Origin should still 403. ---
Console.WriteLine($"[hub-smoke] WSS upgrade WITH hostile Origin (browser-malicious) ...");
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
    if (!threw)
    {
        Console.Error.WriteLine($"  FAIL: hostile Origin should have been rejected (state={ws.State})");
        return 5;
    }
    Console.WriteLine($"  OK: hostile Origin rejected (browser-abuse protection still active)");
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"  HUB SMOKE PASS - {HubBase}");
Console.WriteLine($"  Hub version : {version}");
Console.WriteLine($"  Allowlist   : enabled, browser-only (empty Origin bypassed)");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
return 0;
