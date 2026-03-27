using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

using SpawnDev.BlazorJS;
using SpawnDev.WebTorrent.Demo;
using SpawnDev.WebTorrent.Demo.UnitTests;

Console.WriteLine($"[SpawnDev.WebTorrent.Demo] Build: {BuildTimestamp.Value}");

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddBlazorJSRuntime();
builder.Services.AddSingleton<BrowserTests>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().BlazorJSRunAsync();
