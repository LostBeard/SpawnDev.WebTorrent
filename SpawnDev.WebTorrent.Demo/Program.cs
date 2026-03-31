using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.AsyncFileSystem;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Demo;
using SpawnDev.WebTorrent.Demo.UnitTests;

Console.WriteLine($"[SpawnDev.WebTorrent.Demo] Build: {BuildTimestamp.Value}");

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// BlazorJSRuntime handles all JavaScript interop (required)
builder.Services.AddBlazorJSRuntime();

// Unit tests for browser environment - not required, but useful for testing in CI and as examples of using the API.
builder.Services.AddSingleton<BrowserTests>();

// Adds IPortableCrypto - Cross-platform crypto for BEP 46 signing (ECDSA-P256)
builder.Services.AddPlatformCrypto();

// Adds IAsyncFS - Cross-platform persistent file system (OPFS in browser, native on desktop)
builder.Services.AddAsyncFileSystem();

// WebTorrent services — singletons, start with app via IAsyncBackgroundService
// ServiceWorkerStreamHandler is optional and only needed if you intend to stream torents to media elements using the service worker.
// If not registered, streamign will not be available, but all other WebTorrent features will work as normal.
builder.Services.AddSingleton<ServiceWorkerStreamHandler>();
// WebTorrentClient is the main service for managing torrents, and also implements IAsyncBackgroundService to start automatically with the app.
builder.Services.AddSingleton<WebTorrentClient>();

// HttpClient with BaseAddress set to the app's base URI, for fetching torrent files and other resources relative to the app's location.
builder.Services.AddSingleton(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().BlazorJSRunAsync();
