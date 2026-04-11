using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SpawnDev;
using SpawnDev.AsyncFileSystem;
using SpawnDev.AsyncFileSystem.Native;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.WpfDemo;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // Background service manager (handles IAsyncBackgroundService auto-start)
        services.AddBackgroundServiceManager();

        // Async file system for persistent storage (native filesystem on desktop)
        var storagePath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SpawnDev.WebTorrent");
        System.IO.Directory.CreateDirectory(storagePath);
        services.TryAddSingleton<IAsyncFS>(sp => new AsyncFSNative(storagePath));

        // WebTorrent client with DI-resolved dependencies
        services.AddSingleton<WebTorrentClient>(sp =>
        {
            var asyncFs = sp.GetService<IAsyncFS>();
            var client = new WebTorrentClient(new WebTorrentClientOptions
            {
                AsyncFileSystem = asyncFs,
            });
            // Desktop uses SipSorcery for WebRTC
            client.PeerFactory = (initiator) => new SipSorceryPeer(initiator, trickle: false);
            return client;
        });

        services.AddSingleton<HttpClient>();

        Services = services.BuildServiceProvider();

        // Start background services (IAsyncBackgroundService implementations)
        await Services.StartBackgroundServices();

        // Restore persisted torrents BEFORE showing the window
        var client = Services.GetRequiredService<WebTorrentClient>();
        await client.RestoreFromStorageAsync();

        // Now show the main window - DI is ready, torrents are restored
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
