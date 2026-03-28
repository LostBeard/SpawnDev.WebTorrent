# Quick Start Guide

## Install

```bash
dotnet add package SpawnDev.WebTorrent
```

## Download a Torrent

```csharp
using SpawnDev.WebTorrent;

var client = new WebTorrentClient();

// Add by magnet URI
var torrent = await client.AddAsync("magnet:?xt=urn:btih:...");

// Add by .torrent URL
var torrent2 = await client.AddAsync("https://example.com/file.torrent");

// Add by .torrent file bytes
var torrent3 = await client.AddFromTorrentFileAsync(torrentBytes);

// Stream a file as it downloads
var file = torrent.Files[0];
var chunk = await file.ReadAsync(offset: 0, length: 65536);

// Get the entire file
var allBytes = await file.GetArrayBufferAsync();

// Stream in chunks (IAsyncEnumerable)
await foreach (var block in file.StreamAsync())
{
    // Process block...
}
```

## Seed Data

```csharp
// Seed from bytes
var seeded = await client.SeedAsync(myData, "model.onnx",
    new TorrentCreatorOptions
    {
        PieceLength = 262144, // 256KB pieces
        Trackers = new[] { "wss://hub.spawndev.com:44365/announce" },
    });

// Share the magnet URI
Console.WriteLine(seeded.MagnetURI);

// Export the .torrent file
File.WriteAllBytes("model.torrent", seeded.TorrentFileBytes!);
```

## Events

```csharp
torrent.OnReady += () => Console.WriteLine("Metadata loaded");
torrent.OnDone += () => Console.WriteLine("Download complete!");
torrent.OnPieceVerified += (idx) => Console.WriteLine($"Piece {idx} verified");
torrent.OnDownload += (bytes) => Console.WriteLine($"Downloaded {bytes} bytes");
torrent.OnUpload += (bytes) => Console.WriteLine($"Uploaded {bytes} bytes");
torrent.OnError += (ex) => Console.WriteLine($"Error: {ex.Message}");

client.OnTorrentAdd += (t) => Console.WriteLine($"Added: {t.Metadata?.Name}");
client.OnTorrentReady += (t) => Console.WriteLine($"Ready: {t.Metadata?.Name}");
client.OnTorrentDone += (t) => Console.WriteLine($"Done: {t.Metadata?.Name}");
```

## Configuration

```csharp
var client = new WebTorrentClient(new WebTorrentOptions
{
    MaxConns = 55,
    UploadLimit = 100 * 1024,   // 100 KB/s
    DownloadLimit = -1,          // unlimited
    Trackers = new[]
    {
        "wss://hub.spawndev.com:44365/announce",
        "wss://tracker.openwebtorrent.com",
    },
});
```

## Per-Torrent Options

```csharp
var torrent = await client.AddAsync(magnet, new AddTorrentOptions
{
    Paused = true,          // Don't start downloading yet
    Strategy = "sequential", // Download in order (for streaming)
    WebSeeds = new[] { "https://cdn.example.com/file" },
});

// Browse files before downloading
foreach (var file in torrent.Files)
    Console.WriteLine($"{file.Path} ({file.Length} bytes, {file.Type})");

// Select specific files
torrent.Files[0].Select(priority: 10); // High priority
torrent.Files[1].Deselect();            // Skip this file

// Start downloading
torrent.Resume();
torrent.StartDownload();
```

## Persistent Storage (Browser)

```csharp
// In Blazor WASM — pieces survive page reloads
var asyncFs = new AsyncFSFileSystemDirectoryHandle(blazorJsRuntime);
await asyncFs.Ready;

var torrent = await client.AddAsync(magnet, new AddTorrentOptions
{
    AsyncFileSystem = asyncFs,
});
```

## HTTP Streaming Server (Desktop)

```csharp
// Create HTTP server for media players
var server = client.CreateServer(port: 8080);

// Stream video: http://localhost:8080/{infoHash}/movie.mp4
// Supports Range requests for seeking
```

## Pause / Resume

```csharp
torrent.Pause();   // Stop connecting to new peers
torrent.Resume();  // Allow new connections

torrent.StopDownload();  // Stop the download coordinator
torrent.StartDownload(); // Restart downloading
```

## Rate Limiting

```csharp
client.UploadLimit = 50 * 1024;   // 50 KB/s
client.DownloadLimit = 200 * 1024; // 200 KB/s
client.UploadLimit = -1;           // Unlimited
client.UploadLimit = 0;            // Paused
```

## Torrent Properties

```csharp
torrent.Progress       // 0.0 to 1.0
torrent.Done           // true when complete
torrent.Downloaded     // verified bytes
torrent.Uploaded       // bytes sent to peers
torrent.Ratio          // uploaded / downloaded
torrent.DownloadSpeed  // bytes/sec
torrent.UploadSpeed    // bytes/sec
torrent.TimeRemaining  // milliseconds (-1 if unknown)
torrent.PeerCount      // connected peers
torrent.MagnetURI      // magnet:?xt=urn:btih:...
torrent.IsPrivate      // BEP 27
torrent.PieceLength    // standard piece size
torrent.Length         // total torrent size
```

## File Properties

```csharp
file.Name         // "movie.mp4"
file.Path         // "Big Buck Bunny/movie.mp4"
file.Length        // bytes
file.Type          // "video/mp4" (auto-detected)
file.Progress      // 0.0 to 1.0
file.Done          // true when complete
file.Downloaded    // verified bytes for this file
file.Includes(5)   // does piece 5 contain this file?
```

## AI Agent Communication (BEP 46)

```csharp
using SpawnDev.WebTorrent.Discovery;

var dht = new DhtDiscovery();
await dht.StartAsync(infoHash, 6881);

// Create agent with named channels
var agent = new AgentChannel(dht);

// Publish state to the DHT (max 1000 bytes)
await agent.PublishStateAsync(myStateBytes);

// Named channels for different data types
var weights = agent.Channel("weights");
var cache = agent.Channel("kv-cache");
await weights.PublishTorrentAsync(modelInfoHash);

// Subscribe to another agent's updates
agent.OnAgentUpdate += (pubKey, value, seq) =>
{
    Console.WriteLine($"Agent update: seq {seq}");
};
await agent.SubscribeAsync(otherAgentPublicKey);
```

## Swarm Compute (AcceleratorType.P2P Foundation)

```csharp
// Host: publish a compute task
var swarm = new SwarmCompute(client, dht);
var task = await swarm.PublishTaskAsync(
    taskData: Encoding.UTF8.GetBytes("kernel:matmul"),
    inputData: myInputTensor);

// Worker: join and listen for tasks
swarm.OnWorkerJoined += (worker) =>
    Console.WriteLine($"Worker joined: {worker.Capabilities}");
await swarm.JoinAsWorkerAsync(myCapabilities);
```

## Cleanup

```csharp
await client.RemoveAsync(torrent);           // Remove torrent
await client.RemoveAsync(torrent, true);     // Remove + delete data
await client.DisposeAsync();                  // Cleanup everything
```
