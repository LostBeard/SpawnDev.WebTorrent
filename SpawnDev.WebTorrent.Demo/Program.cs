using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

using SpawnDev.AsyncFileSystem;
using SpawnDev.AsyncFileSystem.BrowserWASM;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Demo;
using SpawnDev.WebTorrent.Demo.UnitTests;

Console.WriteLine($"[SpawnDev.WebTorrent.Demo] Build: {BuildTimestamp.Value}");

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddBlazorJSRuntime();
builder.Services.AddSingleton<BrowserTests>();

// Cross-platform crypto for BEP 46 signing (ECDSA-P256)
if (OperatingSystem.IsBrowser())
{
    builder.Services.AddSingleton<IPortableCrypto, BrowserWASMCrypto>();
}
else
{
    builder.Services.AddSingleton<IPortableCrypto, DotNetCrypto>();
}

// Cross-platform persistent file system (OPFS in browser, native on desktop)
builder.Services.AddSingleton<IAsyncFS, AsyncFSFileSystemDirectoryHandle>();

// WebTorrent services — singletons, start with app via IAsyncBackgroundService
builder.Services.AddSingleton<ServiceWorkerStreamHandler>();
builder.Services.AddSingleton<WebTorrentClient>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().BlazorJSRunAsync();
