# SpawnDev.WebTorrent

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.WebTorrent.svg)](https://www.nuget.org/packages/SpawnDev.WebTorrent)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Pure C# BitTorrent/WebTorrent client and server. No JavaScript dependencies. Runs on desktop (.NET) and browser (Blazor WASM). 461+ tests. 15 BEPs implemented.

## Features

- **Pure C#** — No JavaScript interop, no Node.js, no npm. 100% .NET.
- **Desktop + Browser** — Same library, same API. WPF, console, Blazor WebAssembly.
- **DI Singleton Services** — `WebTorrentClient` and `ServiceWorkerStreamHandler` implement `IAsyncBackgroundService`. Register once, start with the app, inject anywhere.
- **Real WebRTC P2P** — Browser (SpawnDev.BlazorJS) and desktop (SIPSorcery) peers interop seamlessly via the same tracker.
- **15 BEPs** — Full wire protocol, DHT, Fast Extension, ut_metadata, ut_pex, private torrents, magnet file selection, and more.
- **3 Tracker Types** — WebSocket (browser+desktop), HTTP/HTTPS, UDP (desktop).
- **Web Seed Download** — HTTP range requests with multi-file piece assembly (BEP 17/19).
- **Persistent Storage** — Torrents and pieces persist in OPFS (browser) or filesystem (desktop). Survive page reloads. Resume downloading automatically.
- **Media Streaming with Seeking** — Service worker intercepts video/audio range requests and serves pieces on demand. `file.StreamURL`, `file.StreamTo(elem)`, `file.CreateReadStream()`. True seeking — pieces download as the video plays.
- **Service Worker** — `webtorrent-sw.js` ships with the library. Handles Cross-Origin-Isolation headers, Blazor loading, and torrent streaming via MessageChannel. One `<script>` tag and your app streams.
- **System.IO.Stream** — `file.CreateReadStream()` returns a seekable .NET `Stream` backed by torrent pieces. Use with any API that takes a Stream.
- **Random-Access Streaming** — Read any byte range from a torrent file as it downloads. Pieces download on demand. Perfect for ML model weight streaming.
- **Seeding** — Upload pieces to requesting peers with configurable rate limiting.
- **Speed Tracking** — Real-time download/upload bytes/sec per torrent.
- **AI Agent Communication** — BEP 46 DHT mutable items with real ECDSA-P256 signing via SpawnDev.BlazorJS.Cryptography. AgentChannel pub/sub for shared AI state.
- **HuggingFace Integration** — Optional server extension that proxies HuggingFace model CDN with local caching and automatic torrent generation.
- **Custom Wire Extensions** — `UseExtension()` factory pattern (same as JS WebTorrent `wire.use()`). Build custom P2P protocols on top of the BitTorrent wire — distributed compute, AI agents, anything. Extensions negotiate via BEP 10.
- **.torrent Creation** — Create and parse .torrent files. Complete Bencode encoder/decoder.
- **452+ Unit Tests** — Every BEP tested, ECDSA crypto verified in browser, service worker streaming verified end-to-end, security tests for signature verification.

## Packages

| Package | Description |
|---------|-------------|
| [SpawnDev.WebTorrent](https://www.nuget.org/packages/SpawnDev.WebTorrent) | Client library — torrents, peers, streaming |
| [SpawnDev.WebTorrent.Server](https://www.nuget.org/packages/SpawnDev.WebTorrent.Server) | Server library — tracker, web seed |

## Quick Start — Blazor WebAssembly (DI)

```csharp
// Program.cs
using SpawnDev.AsyncFileSystem;
using SpawnDev.AsyncFileSystem.BrowserWASM;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.WebTorrent;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddBlazorJSRuntime();

// Cross-platform crypto for BEP 46 signing
if (OperatingSystem.IsBrowser())
    builder.Services.AddSingleton<IPortableCrypto, BrowserWASMCrypto>();
else
    builder.Services.AddSingleton<IPortableCrypto, DotNetCrypto>();

// Persistent file system (OPFS in browser)
builder.Services.AddSingleton<IAsyncFS, AsyncFSFileSystemDirectoryHandle>();

// WebTorrent services — start with the app via IAsyncBackgroundService
builder.Services.AddSingleton<ServiceWorkerStreamHandler>();
builder.Services.AddSingleton<WebTorrentClient>();

await builder.Build().BlazorJSRunAsync();
```

```html
<!-- index.html — replaces blazor.webassembly.js -->
<script src="webtorrent-sw.js"></script>
```

```csharp
// Any page or service — inject the singleton
@inject WebTorrentClient Client

// Download a torrent
var torrent = await Client.AddAsync("magnet:?xt=urn:btih:...");

// Stream a video with seeking
var url = torrent.Files[0].StreamURL;
// <video src="@url" controls></video>

// Or set directly on an element
torrent.Files[0].StreamTo(videoElement);

// Get a .NET Stream
using var stream = torrent.Files[0].CreateReadStream();
var buffer = new byte[4096];
var bytesRead = await stream.ReadAsync(buffer);
stream.Position = 1000000; // seek
bytesRead = await stream.ReadAsync(buffer);

// Seed data
var seeded = await Client.SeedAsync(myBytes, "model.onnx");
Console.WriteLine(seeded.MagnetURI);
```

Torrents persist across page reloads — pieces stored in OPFS, metadata restored on startup.

## File API

Feature parity with the [WebTorrent JS File API](https://github.com/webtorrent/webtorrent/blob/master/docs/api.md#file-api):

| Property/Method | Description |
|----------------|-------------|
| `file.Name` | File name |
| `file.Path` | File path within torrent |
| `file.Length` / `file.Size` | File size in bytes |
| `file.Type` | MIME type |
| `file.Downloaded` | Verified bytes received |
| `file.Progress` | Download progress (0.0 to 1.0) |
| `file.Done` | Whether fully downloaded |
| `file.StreamURL` | Service worker streaming URL |
| `file.StreamTo(elem)` | Set element src to streaming URL |
| `file.CreateReadStream()` | Seekable .NET `System.IO.Stream` |
| `file.ReadAsync(offset, length)` | Random-access byte read |
| `file.GetArrayBufferAsync()` | Full file as byte array |
| `file.Select(priority)` | Prioritize this file's pieces |
| `file.Deselect()` | Deprioritize |
| `file.Includes(pieceIndex)` | Check if piece belongs to file |
| `file.OnDone` | Event when download completes |

## Wire Extensions (BEP 10)

Register custom wire protocol extensions that participate in BEP 10 negotiation — same pattern as JS WebTorrent's `wire.use()`:

```csharp
// Register before adding torrents — extension factory creates one per peer
client.UseExtension(() => new MyComputeExtension());

// Or on a specific swarm
swarm.UseExtension(() => new MyComputeExtension());
```

Create custom extensions by extending `WireExtension`:

```csharp
public class MyComputeExtension : WireExtension
{
    public override string Name => "sd_compute";

    public override Task HandleMessageAsync(byte[] payload)
    {
        // Handle incoming messages from peers
        var msg = ParseMessage(payload);
        return Task.CompletedTask;
    }

    public override Dictionary<string, object>? GetHandshakeData()
    {
        // Include data in BEP 10 handshake (optional)
        return new() { ["capabilities"] = new List<object> { "gpu", "wasm" } };
    }

    public override void ProcessHandshakeData(Dictionary<string, object> data)
    {
        // Process peer's handshake data
    }

    public async Task SendAsync(WireProtocol wire, byte[] data)
    {
        if (!IsSupported) return; // peer doesn't have this extension
        await wire.SendExtensionMessageAsync(RemoteId, data);
    }
}
```

Extensions are created per-peer via factory, registered **before** the BEP 10 handshake so `IsSupported` and `RemoteId` are set correctly. Use this for custom P2P protocols on top of the BitTorrent wire (e.g., distributed compute, AI agent communication).

## Service Worker — Media Streaming with Seeking

`webtorrent-sw.js` ships with the NuGet package and deploys to your app root automatically. It:

1. Registers itself as a service worker
2. Adds Cross-Origin-Isolation headers (COOP/COEP) for SharedArrayBuffer
3. Waits for the SW to be ready, then loads Blazor
4. Intercepts `/webtorrent/{infoHash}/{fileIndex}` requests
5. Forwards range requests to the `ServiceWorkerStreamHandler` singleton via MessageChannel
6. Streams piece data back as a `ReadableStream` with proper `206 Partial Content` headers

Health check: fetch `/webtorrent-sw-check` to verify the SW is active.

## Quick Start — Server

```csharp
using SpawnDev.WebTorrent.Server;

var tracker = new TorrentTracker();
var webSeed = new WebSeedServer("seed-data");

var app = WebApplication.CreateBuilder(args).Build();
app.UseWebSockets();
app.MapWebTorrentServer(tracker, webSeed);
app.Run();
```

## Quick Start — HuggingFace Proxy

```csharp
using SpawnDev.WebTorrent.Server.HuggingFace;

var proxy = new HuggingFaceProxy(new HuggingFaceProxyOptions
{
    CacheDirectory = "hf-cache",
});
app.MapHuggingFaceProxy(proxy);

// Clients access: https://your-server/hf/{repoId}/{filePath}
// Auto-caches from HuggingFace CDN on first request
// Generates .torrent files for P2P distribution
```

## Demo Apps

| App | Platform | Features |
|-----|----------|----------|
| Blazor WASM Demo | Browser | Full torrent client UI, media streaming with seeking, WebRTC P2P, OPFS persistent storage, seeding, .torrent creation, torrent persistence across reloads |
| WPF Desktop Demo | Windows | Full torrent client UI, media player with seeking, drag-drop .torrent files, SIPSorcery WebRTC |

Both demos connect to the same trackers and can P2P with each other.

## BEP Support

15 BitTorrent Enhancement Proposals implemented:

| BEP | Title | Desktop | Browser |
|-----|-------|---------|---------|
| 3 | BitTorrent Protocol | Yes | Yes |
| 5 | DHT (Kademlia) | Yes | Via relay |
| 44 | DHT Storage | Yes | Via relay |
| 46 | Mutable Items (AI Agents) | Yes | Yes |
| 6 | Fast Extension | Yes | Yes |
| 9 | Magnet Links / ut_metadata | Yes | Yes |
| 10 | Extension Protocol | Yes | Yes |
| 11 | Peer Exchange (ut_pex) | Yes | Yes |
| 15 | UDP Tracker | Yes | N/A |
| 17/19 | Web Seeds | Yes | Yes |
| 20 | Peer ID Conventions | Yes | Yes |
| 23 | Compact Peer Lists | Yes | Yes |
| 27 | Private Torrents | Yes | Yes |
| 53 | Magnet File Selection | Yes | Yes |

See [Docs/bep-support.md](Docs/bep-support.md) for full details.

## Why This Exists

AI models are big. CDNs can't scale when every user downloads the same 2GB model. SpawnDev.WebTorrent turns every browser into a peer — the more users, the faster delivery. Built for [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML), the GPU-accelerated ML library for Blazor WebAssembly.

## Architecture

```
Browser Client                    Desktop Client
+---------------+                 +------------------+
| WebTorrent    |                 | WebTorrent       |
| Client        |                 | Client           |
|               |                 |                  |
| WebRTC (P2P)<-+------+-------->+ SIPSorcery (P2P) |
| BlazorJS      |      |         | RTCPeerConnection|
|               |      |         |                  |
| OPFS Storage  |      |         | FileChunkStore   |
+---------------+      |         +------------------+
        |               |                |
        v               v                v
+-------------------------------------------+
| hub.spawndev.com                          |
| TorrentTracker (WebSocket signaling)      |
| WebSeedServer (HTTP range fallback)       |
| HuggingFaceProxy (model CDN cache)        |
+-------------------------------------------+
```

## Documentation

| Doc | Description |
|-----|-------------|
| [Quick Start](Docs/quickstart.md) | Full API examples: download, seed, stream, events, config |
| [Architecture](Docs/architecture.md) | Project structure, data flow, transport layer, wire protocol |
| [Peer ID](Docs/peer-id.md) | Azureus-style peer ID convention (`-SD0110-`) and version history |
| [Trackers](Docs/trackers.md) | Default tracker list, running your own tracker, protocol details |
| [BEP Support](Docs/bep-support.md) | BitTorrent Enhancement Proposal implementation status |
| [AI Agents](Docs/ai-agents.md) | DHT pub/sub for AI agent communication, shared compute, BEP 46 |
| [Service Worker](Docs/service-worker.md) | Media streaming, COI headers, Blazor loading, range requests |
| [Deployment](Docs/deployment.md) | Production server setup, GitHub Pages demo, local development |

## Credits

Built by Todd Tanner ([@LostBeard](https://github.com/LostBeard)) and the SpawnDev team.

## License

MIT
