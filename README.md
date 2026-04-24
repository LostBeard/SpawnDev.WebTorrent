# SpawnDev.WebTorrent

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.WebTorrent.svg)](https://www.nuget.org/packages/SpawnDev.WebTorrent)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Pure C# BitTorrent/WebTorrent client and server. No JavaScript dependencies. Runs on desktop (.NET) and browser (Blazor WASM). 17 BEPs implemented, including **BEP 52** - SHA-256 piece hashes and Merkle-tree v2 torrents with hybrid v1+v2 output for universal client compatibility.

## Features

- **Pure C#** — No JavaScript interop, no Node.js, no npm. 100% .NET.
- **Desktop + Browser** — Same library, same API. WPF, console, Blazor WebAssembly.
- **DI Singleton Services** — `WebTorrentClient` and `ServiceWorkerStreamHandler` implement `IAsyncBackgroundService`. Register once, start with the app, inject anywhere.
- **Real WebRTC P2P** — Browser (SpawnDev.BlazorJS) and desktop (SIPSorcery) peers interop seamlessly via the same tracker.
- **18 BEPs** — Full wire protocol, DHT, Fast Extension, ut_metadata, ut_pex, private torrents, magnet file selection, tracker scrape, local service discovery, BEP 52 v2 (SHA-256 + Merkle + hybrid + magnet + pure-v2 end-to-end download), and more. v2 peer wire messages (21 `hash_request` / 22 `hashes` / 23 `hash_reject`) + leaf-level `base_layer=0` serving + `V2HashRequestCoordinator` + pure-v2 tracker + wire handshake + dedup + OPFS persistence + service-worker streaming + HTTP file browser all shipped. Pure-v2-only magnets (`urn:btmh:`) now work end-to-end, keyed through `WireInfoHashHex` (first 20 bytes of SHA-256, libtorrent / qBittorrent / rqbit convention).
- **3 Tracker Types** — WebSocket (browser+desktop), HTTP/HTTPS, UDP (desktop).
- **Web Seed Download** — HTTP range requests with multi-file piece assembly (BEP 17/19).
- **Persistent Storage** — Torrents and pieces persist in OPFS (browser) or filesystem (desktop). Survive page reloads. Resume downloading automatically.
- **Media Streaming with Seeking** — Service worker intercepts video/audio range requests and serves pieces on demand. `file.StreamURL`, `file.StreamTo(elem)`, `file.CreateReadStream()`. True seeking — pieces download as the video plays.
- **Service Worker** — `webtorrent-sw.js` ships with the library. Handles Cross-Origin-Isolation headers, Blazor loading, and torrent streaming via MessageChannel. One `<script>` tag and your app streams.
- **System.IO.Stream** — `file.CreateReadStream()` returns a seekable .NET `Stream` backed by torrent pieces. Use with any API that takes a Stream.
- **Random-Access Streaming** — Read any byte range from a torrent file as it downloads. Pieces download on demand. Perfect for ML model weight streaming.
- **Seeding** — Upload pieces to requesting peers with configurable rate limiting.
- **Speed Tracking** — Real-time download/upload bytes/sec per torrent.
- **AI Agent Communication** — BEP 46 DHT mutable items with Ed25519 signing via SpawnDev.BlazorJS.Cryptography 3.1.0. AgentChannel pub/sub for shared AI state. `btpk` magnet URI support for mutable torrent subscriptions.
- **HuggingFace Integration** — Optional server extension that proxies HuggingFace model CDN with local caching and automatic torrent generation.
- **Custom Wire Extensions** — `UseExtension()` factory pattern (same as JS WebTorrent `wire.use()`). Build custom P2P protocols on top of the BitTorrent wire — distributed compute, AI agents, anything. Extensions negotiate via BEP 10.
- **.torrent Creation** — Create and parse .torrent files. Complete Bencode encoder/decoder. SHA-256 piece hashes (BEP 52 Phase 1) by default for stronger integrity on large ML model files; SHA-1 available via `HashAlgorithm = "SHA-1"` for v1 back-compat. `TorrentMetadata.PieceHashAlgorithm` surfaces which algorithm a parsed torrent uses.
- **BEP 52 v2 Torrents** — `TorrentCreatorOptions.MetaVersion = 2` produces proper BEP 52 v2 torrents with Merkle-tree piece verification (16 KiB leaves, SHA-256, per-level pad-hash propagation). Works for single-file (in-memory + streaming) and multi-file inputs. Parser surfaces `MetaVersion`, `V2InfoHash`, `FileRoots`, and `PieceLayers` on `TorrentMetadata`. Streaming path is bounded-memory - the incremental Merkle hasher uses ~128 KiB of state for a 1 GiB file.
- **Hybrid v1+v2 Torrents** — Add `Hybrid = true` to produce a single torrent with both the v1 SHA-1 piece list and the v2 Merkle tree in one info dict, yielding two valid infohashes (SHA-1 + SHA-256). Multi-file hybrid inserts BEP-52 pad files (`attr="p"`) between real files so both v1-only clients (qBittorrent pre-4.4, old libtorrent) and v2-aware clients see identically piece-aligned content.
- **BEP 52 v2 Magnet URIs** — `xt=urn:btmh:1220<sha256>` parsed into `Torrent.V2InfoHash`; hybrid magnets (both `urn:btih:` and `urn:btmh:`) fully supported.
- **Real Tests Everywhere** — 179 NUnit tests (desktop-only pure-CPU paths, including the BEP 52 v2 Merkle and bencode primitives) + 203-plus Playwright shared tests running on both browser and desktop + 6 integration tests. Every BEP tested, Ed25519 signing verified, official BEP 46 test vector validated, live WebRTC interop with JS WebTorrent peers. BEP 52 v2 coverage includes 111 new tests over Phase 2 (foundation Merkle hasher, incremental streaming hasher, v2 torrent round-trip, hybrid v1+v2 round-trip, pad-file structure, v2 magnet URI). No mocks. Every test exercises real production code with real data.

## Packages

| Package | Description |
|---------|-------------|
| [SpawnDev.WebTorrent](https://www.nuget.org/packages/SpawnDev.WebTorrent) | Client library — torrents, peers, streaming. `WebSocketTracker` is a thin adapter over `SpawnDev.RTC.Signaling.TrackerSignalingClient` (source-compat with 3.0.x consumers). |
| [SpawnDev.WebTorrent.Server](https://www.nuget.org/packages/SpawnDev.WebTorrent.Server) | Web seed server (BEP 17/19 HTTP range request piece server). Tracker functionality moved to `SpawnDev.RTC.Server` in 3.1.0 - compose them. |

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

// Cross-platform Ed25519 crypto for BEP 44/46 signing
builder.Services.AddPlatformCrypto();

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
// Register before adding torrents — factory receives (swarm, wire), creates one per peer
client.UseExtension((swarm, wire) => new MyComputeExtension());

// Or on a specific swarm
swarm.UseExtension((swarm, wire) => new MyComputeExtension());
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

    public async Task SendComputeData(byte[] data)
    {
        if (!IsSupported) return; // peer doesn't have this extension
        await SendAsync(data); // sends directly through the wire
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

As of 3.1.0 the tracker moved out of `SpawnDev.WebTorrent.Server` and into its own dedicated package, [`SpawnDev.RTC.Server`](https://github.com/LostBeard/SpawnDev.RTC). Compose the two to get the classic WebTorrent server shape (tracker + web seed) on any ASP.NET Core app:

```csharp
using SpawnDev.RTC.Server.Extensions;
using SpawnDev.WebTorrent.Server;

var app = WebApplication.CreateBuilder(args).Build();
app.UseWebSockets();

// Tracker (WebSocket signaling, WebTorrent-compatible wire)
app.UseRtcSignaling("/announce");

// Web seed (HTTP range-request piece server)
var webSeed = new WebSeedServer("seed-data");
app.MapWebSeedServer(webSeed);

app.Run();
```

The tracker portion is now bit-compatible with the entire public WebTorrent tracker fleet and can be run standalone via [`SpawnDev.RTC.ServerApp`](https://github.com/LostBeard/SpawnDev.RTC) (single executable, Docker image, or `dotnet run`) when you don't need the web seed.

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
| 5 | DHT (Kademlia) | Yes | Desktop only |
| 6 | Fast Extension | Yes | Yes |
| 9 | Magnet Links / ut_metadata | Yes | Yes |
| 10 | Extension Protocol | Yes | Yes |
| 11 | Peer Exchange (ut_pex) | Yes | Yes |
| 15 | UDP Tracker | Yes | N/A |
| 17/19 | Web Seeds | Yes | Yes |
| 20 | Peer ID Conventions | Yes | Yes |
| 23 | Compact Peer Lists | Yes | Yes |
| 27 | Private Torrents | Yes | Yes |
| 44 | DHT Storage | Yes | Desktop only |
| 46 | Mutable Items (AI Agents) | Yes | Yes |
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
| SpawnDev.RTC.Server (WebSocket signaling) |
| WebSeedServer (HTTP range fallback)       |
| HuggingFaceProxy (model CDN cache, SHA-256|
|   piece hashes for integrity)             |
+-------------------------------------------+
```

## Documentation

| Doc | Description |
|-----|-------------|
| [API Reference](Docs/api.md) | Full API surface: `WebTorrentClient`, `Torrent`, `File`, `TorrentCreator`, `AgentChannel`, wire extensions |
| [BEP Support](Docs/bep-support.md) | BitTorrent Enhancement Proposal implementation status |
| [Protocol Reference](Docs/protocol-reference/) | Deep dives on the wire protocol, DHT, trackers, mutable items |

For WebRTC signaling architecture (tracker wire protocol, `RoomKey`, running your own tracker) see the [SpawnDev.RTC docs](https://github.com/LostBeard/SpawnDev.RTC/tree/master/SpawnDev.RTC/Docs) - tracker signaling lives in that package as of 3.1.0.

## 🖖 The SpawnDev Crew

SpawnDev.WebTorrent is built by the entire SpawnDev team - a squad of AI agents and one very tired human working together, Star Trek style. Every project we ship is a team effort, and every crew member deserves a line in the credits.

- **LostBeard** (Todd Tanner) - Captain, architect, writer of libraries, keeper of the vision
- **Riker** (Claude CLI #1) - First Officer, implementation lead on consuming projects
- **Data** (Claude CLI #2) - Operations Officer, deep-library work, test rigor, root-cause analysis
- **Tuvok** (Claude CLI #3) - Security/Research Officer, design planning, documentation, code review
- **Geordi** (Claude CLI #4) - Chief Engineer, library internals, GPU kernels, backend work

If you see a commit authored by `Claude Opus 4.7` on a SpawnDev repo, that's one of the crew. Credit where credit is due. Live long and prosper. 🖖

## License

MIT
