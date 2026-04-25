using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpawnDev.RTC.Server;
using SpawnDev.RTC.Server.Extensions;

namespace PlaywrightMultiTest;

/// <summary>
/// Spins up an in-process SpawnDev.RTC.Server WebSocket tracker on a free TCP
/// port. Lets desktop unit tests avoid depending on hub.spawndev.com (which is
/// Origin-allowlisted for browser clients and, by design, not reachable from the
/// CI offline profile). Uses the same pattern as SpawnDev.RTC's own
/// OriginAllowlist_E2E tests.
/// </summary>
public sealed class LocalTrackerFixture : IAsyncDisposable
{
    public WebApplication App { get; }
    public TrackerSignalingServer Tracker { get; }
    public int Port { get; }
    public string WsAnnounceUrl => $"ws://127.0.0.1:{Port}/announce";

    private LocalTrackerFixture(WebApplication app, TrackerSignalingServer tracker, int port)
    {
        App = app;
        Tracker = tracker;
        Port = port;
    }

    public static async Task<LocalTrackerFixture> StartAsync()
    {
        var port = GetFreeTcpPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        var tracker = app.UseRtcSignaling("/announce", new TrackerServerOptions());
        await app.StartAsync();
        return new LocalTrackerFixture(app, tracker, port);
    }

    public async ValueTask DisposeAsync()
    {
        // StopAsync with a short hard cap so lingering WebSocket connections
        // (announce clients that haven't closed cleanly yet) don't block teardown
        // forever. Kestrel's default is "wait indefinitely" which deadlocked the
        // Desktop_SeedAndAnnounce_TrackerConnects test under 30s NUnit timeout
        // when the client's tracker WS stayed connected past client.DisposeAsync.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await App.StopAsync(cts.Token);
        }
        catch { }
        await App.DisposeAsync();
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
