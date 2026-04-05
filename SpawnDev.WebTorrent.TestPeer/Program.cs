using SpawnDev.WebTorrent;

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
///   PIECE: 0                           # When a piece is verified (downloading)
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
Console.Error.WriteLine($"[TestPeer] Peer ID: {client.PeerId[..8]}");

if (mode == "seed")
{
    // Generate deterministic test data (seeded by size for reproducibility)
    var data = new byte[dataSize];
    var rng = new Random(dataSize);
    rng.NextBytes(data);

    // SeedAsync creates torrent, writes pieces to store, and connects to trackers
    var torrent = await client.SeedAsync($"test-{dataSize}.bin", data,
        new TorrentCreatorOptions
        {
            PieceLength = 16384,
            Trackers = new[] { trackerUrl },
        });

    // Machine-readable output
    Console.WriteLine($"HASH: {torrent.InfoHashHex}");
    Console.WriteLine($"MAGNET: {torrent.ComputedMagnetUri}");
    Console.WriteLine($"PIECES: {torrent.PieceCount}");
    Console.WriteLine($"SIZE: {dataSize}");

    // Wire events for logging
    torrent.OnWire += (wire, addr) => Console.Error.WriteLine($"[TestPeer] Peer connected: {addr}");

    Console.WriteLine("READY");
    Console.Error.WriteLine("[TestPeer] Seeding... Press Ctrl+C to stop.");

    // Keep running until killed
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
}
else if (mode == "download" && magnetUri != null)
{
    // Add torrent from magnet — tracker connections handled internally
    var torrent = client.Add(magnetUri);
    Console.WriteLine($"HASH: {torrent.InfoHashHex}");

    torrent.OnPieceVerified += (idx) => Console.WriteLine($"PIECE: {idx}");
    torrent.OnDone += () => Console.WriteLine("DONE");
    torrent.OnWire += (wire, addr) => Console.Error.WriteLine($"[TestPeer] Peer connected: {addr}");

    Console.WriteLine("DOWNLOADING");
    Console.Error.WriteLine("[TestPeer] Downloading... Press Ctrl+C to stop.");

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
}
