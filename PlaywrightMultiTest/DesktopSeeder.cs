using SpawnDev.WebTorrent;
using System.Text.Json;

namespace PlaywrightMultiTest;

/// <summary>
/// Desktop WebTorrent seeder for cross-platform P2P tests.
/// Started by GlobalSetup before browser tests run. Seeds deterministic test data
/// via hub.spawndev.com tracker using SipSorcery WebRTC (desktop transport).
/// Browser tests download from this seeder to verify desktop↔browser interop.
/// </summary>
public class DesktopSeeder : IAsyncDisposable
{
    private WebTorrentClient? _client;
    private Torrent? _swarm;

    private static readonly string LogFile = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "desktop_seeder.log");

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [DesktopSeeder] {msg}";
        Console.Error.WriteLine(line);
        try { File.AppendAllText(LogFile, line + "\n"); } catch { }
    }

    /// <summary>Magnet URI the browser test should use to download.</summary>
    public string? MagnetUri { get; private set; }

    /// <summary>The test data bytes being seeded.</summary>
    public byte[]? TestData { get; private set; }

    /// <summary>Whether the seeder is actively seeding.</summary>
    public bool IsSeeding => _swarm != null && MagnetUri != null;

    /// <summary>
    /// Create deterministic test data, create a torrent, and start seeding
    /// via the real tracker at hub.spawndev.com.
    /// </summary>
    public async Task StartAsync()
    {
        try { File.WriteAllText(LogFile, ""); } catch { } // clear log

        // Deterministic test data — browser test generates the same bytes for verification
        TestData = new byte[32768]; // 2 pieces at 16KB
        for (int i = 0; i < TestData.Length; i++)
            TestData[i] = (byte)((i * 7 + 13) % 256);

        _client = new WebTorrentClient();

        // Enable verbose logging for diagnostics
        WebTorrentClient.VerboseLogging = true;

        // Seed the data via real tracker
        _swarm = await _client.SeedAsync("crossplatform-test.bin", TestData,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { "wss://hub.spawndev.com:44365/announce" },
            });
        MagnetUri = _swarm.ComputedMagnetUri;

        Log($"Seeding: {MagnetUri}");
        Log($"InfoHash: {_swarm.InfoHashHex}");

        // ConnectTrackersFromMetadataAsync is fire-and-forget in SeedAsync.
        // Wait for the tracker connection to actually establish.
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (_swarm.PeerCount > 0) break;
        }
        Log($"After tracker wait: Ready={_swarm.Ready}, Done={_swarm.Done}, Peers={_swarm.PeerCount}");

        _swarm.OnWire += (wire, addr) => Log($"Peer connected: {addr}");
    }

    /// <summary>
    /// Write the magnet URI to a JSON file in the published wwwroot
    /// so the browser test can fetch it.
    /// </summary>
    public void WriteTestConfig(string wwwrootPath)
    {
        var config = new
        {
            magnetUri = MagnetUri,
            infoHash = _swarm?.InfoHashHex,
            dataLength = TestData?.Length,
            pieceLength = 16384,
        };
        var json = JsonSerializer.Serialize(config);
        var path = Path.Combine(wwwrootPath, "_test-desktop-seeder.json");
        File.WriteAllText(path, json);
        Console.Error.WriteLine($"[DesktopSeeder] Config written to: {path}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_swarm != null)
        {
            await _swarm.DisposeAsync();
            _swarm = null;
        }
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        MagnetUri = null;
        Console.Error.WriteLine("[DesktopSeeder] Stopped.");
    }
}
