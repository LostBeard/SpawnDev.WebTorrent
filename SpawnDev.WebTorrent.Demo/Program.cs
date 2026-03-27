using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.BlazorJS;
using SpawnDev.WebTorrent.Demo.UnitTests;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddBlazorJSRuntime();

// Register test types for UnitTestsView discovery
builder.Services.AddSingleton<BrowserTests>();

builder.RootComponents.Add<SpawnDev.WebTorrent.Demo.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().BlazorJSRunAsync();
