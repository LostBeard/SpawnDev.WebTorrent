using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.AsyncFileSystem;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Demo;

Console.WriteLine($"[SpawnDev.WebTorrent.Demo] Build: {BuildTimestamp.Value}");

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// BlazorJSRuntime handles all JavaScript interop (required for SpawnDev.BlazorJS)
builder.Services.AddBlazorJSRuntime();

// Cross-platform crypto for Ed25519 signing (BEP 44)
builder.Services.AddPlatformCrypto();

// Async file system for persistent storage (OPFS in browser)
builder.Services.AddAsyncFileSystem();

// Service worker stream handler for media streaming (implements IAsyncBackgroundService)
builder.Services.AddSingleton<ServiceWorkerStreamHandler>();

// WebTorrent client — uses RtcPeer (SpawnDev.RTC) for WebRTC, OPFS for persistence, SW streaming
builder.Services.AddSingleton<WebTorrentClient>(sp =>
{
    var asyncFs = sp.GetService<IAsyncFS>();
    var streamHandler = sp.GetService<ServiceWorkerStreamHandler>();
    var client = new WebTorrentClient(new WebTorrentClientOptions
    {
        AsyncFileSystem = asyncFs,
        StreamHandler = streamHandler,
    });
    // Restore persisted torrents (fire and forget — completes before first page render)
    _ = client.RestoreFromStorageAsync();
    return client;
});

// Register BrowserTests for test discovery via Tests.razor UnitTestsView
builder.Services.AddSingleton<BrowserTests>();

builder.Services.AddSingleton(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().BlazorJSRunAsync();
