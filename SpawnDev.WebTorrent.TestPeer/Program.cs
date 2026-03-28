using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

/// <summary>
/// Test peer console app for controlled swarm testing.
/// Creates test data, seeds it, connects to tracker, and serves pieces.
///
/// Usage:
///   dotnet run                          # Seed random 64KB, connect to local tracker
///   dotnet run -- --size 131072         # Seed 128KB
///   dotnet run -- --tracker wss://...   # Use specific tracker
///   dotnet run -- --magnet "magnet:..." # Download instead of seed
///
/// Output:
///   MAGNET: magnet:?xt=urn:btih:...    # Machine-readable magnet URI for other clients
///   HASH: dd8255ec...                   # Info hash hex
///   READY                              # Seeding and ready for connections
///   PIECE_SERVED: 0                    # When a piece is served to a peer
///   DONE                               # All pieces downloaded (if downloading)
/// </summary>

var trackerUrl = "ws://localhost:5561/announce";
var dataSize = 65536;
string? magnetUri = null;
var mode = "seed"; // "seed" or "download"

// Parse args
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--tracker" when i + 1 < args.Length: trackerUrl = args[++i]; break;
        case "--size" when i + 1 < args.Length: dataSize = int.Parse(args[++i]); break;
        case "--magnet" when i + 1 < args.Length: magnetUri = args[++i]; mode = "download"; break;
    }
}

Console.Error.WriteLine($"[TestPeer] Mode: {mode}, Size: {dataSize}, Tracker: {trackerUrl}");

await using var client = new WebTorrentClient();
Console.Error.WriteLine($"[TestPeer] Peer ID: {System.Text.Encoding.ASCII.GetString(client.PeerId, 0, 8)}");

if (mode == "seed")
{
    // Generate deterministic test data (seeded by size for reproducibility)
    var data = new byte[dataSize];
    var rng = new Random(dataSize);
    rng.NextBytes(data);

    var swarm = await client.SeedAsync(data, $"test-{dataSize}.bin",
        new TorrentCreatorOptions
        {
            PieceLength = 16384,
            Trackers = new[] { trackerUrl },
        });

    var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
    var magnet = swarm.MagnetURI;

    // Machine-readable output
    Console.WriteLine($"HASH: {hash}");
    Console.WriteLine($"MAGNET: {magnet}");
    Console.WriteLine($"PIECES: {swarm.PieceManager!.PieceCount}");
    Console.WriteLine($"SIZE: {dataSize}");

    // Connect to tracker
    var tracker = new WebSocketTrackerClient(trackerUrl, client.PeerId);
    tracker.OnPeer += (p) => Console.Error.WriteLine($"[TestPeer] Peer discovered: {p.Address}");
    tracker.OnAnnounceResponse += (s, l) => Console.Error.WriteLine($"[TestPeer] Announce: {s}S/{l}L");

    // Wire up piece serve logging
    swarm.OnUpload += (bytes) => Console.WriteLine($"PIECE_SERVED: {bytes}");

    try
    {
        await tracker.StartAsync(swarm.InfoHash, 0);
        Console.Error.WriteLine("[TestPeer] Tracker connected");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[TestPeer] Tracker failed: {ex.Message}");
    }

    Console.WriteLine("READY");
    Console.Error.WriteLine("[TestPeer] Seeding... Press Ctrl+C to stop.");

    // Keep running until killed
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
}
else if (mode == "download" && magnetUri != null)
{
    var swarm = await client.AddAsync(magnetUri);
    Console.WriteLine($"HASH: {Convert.ToHexString(swarm.InfoHash).ToLowerInvariant()}");

    // Try to fetch .torrent from xs= URL
    foreach (var part in magnetUri.Split('&'))
    {
        var p = part.Contains('?') ? part.Split('?').Last() : part;
        var eq = p.IndexOf('=');
        if (eq >= 0 && p[..eq] == "xs")
        {
            var url = Uri.UnescapeDataString(p[(eq + 1)..]);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var torrentBytes = await http.GetByteArrayAsync(url);
            var metadata = TorrentParser.Parse(torrentBytes);
            if (metadata.InfoHash.SequenceEqual(swarm.InfoHash))
            {
                swarm.SetMetadata(metadata);
                foreach (var ws in metadata.UrlList) swarm.AddWebSeed(ws.TrimEnd('/'));
            }
        }
    }

    // Connect to tracker
    var tracker = new WebSocketTrackerClient(trackerUrl, client.PeerId);
    tracker.OnPeer += (p) =>
    {
        Console.Error.WriteLine($"[TestPeer] Peer: {p.Address}");
        swarm.AddPeer(p);
    };
    await tracker.StartAsync(swarm.InfoHash, 0);

    swarm.OnPieceVerified += (idx) => Console.WriteLine($"PIECE: {idx}");
    swarm.OnDone += () => Console.WriteLine("DONE");

    swarm.StartDownload();

    Console.WriteLine("DOWNLOADING");
    Console.Error.WriteLine("[TestPeer] Downloading... Press Ctrl+C to stop.");

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
}
