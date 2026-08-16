# SpawnDev.WebTorrent

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.WebTorrent.svg)](https://www.nuget.org/packages/SpawnDev.WebTorrent)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

> 💜 **Built and maintained by one independent developer** — no company, no overhead, just code. If SpawnDev.WebTorrent saves you time, please consider [**sponsoring its development »**](https://github.com/sponsors/LostBeard). Sponsorship is what keeps it alive and maintained.

Pure C# BitTorrent/WebTorrent client and server. No JavaScript dependencies. Runs on desktop (.NET) and browser (Blazor WASM). 18 BEPs implemented, including **BEP 52** - SHA-256 piece hashes and Merkle-tree v2 torrents with hybrid v1+v2 output for universal client compatibility.

## Features

- **Pure C#** — No JavaScript interop, no Node.js, no npm. 100% .NET.
- **Desktop + Browser** — Same library, same API. WPF, console, Blazor WebAssembly.
- **DI Singleton Services** — `WebTorrentClient` and `ServiceWorkerStreamHandler` implement `IAsyncBackgroundService`. Register once, start with the app, inject anywhere.
- **Real WebRTC P2P** — Cross-platform via [SpawnDev.RTC](https://github.com/LostBeard/SpawnDev.RTC). One `RtcPeer` type backs browser (SpawnJS `RTCPeerConnection`) and desktop (a SipSorcery fork with proven DTLS/SRTP). Browser and desktop peers interop seamlessly through the same tracker.
- **18 BEPs** — Full wire protocol, DHT, Fast Extension, ut_metadata, ut_pex, private torrents, magnet file selection, tracker scrape, local service discovery, BEP 52 v2 (SHA-256 + Merkle + hybrid + magnet + pure-v2 end-to-end download), and more. v2 peer wire messages (21 `hash_request` / 22 `hashes` / 23 `hash_reject`) + leaf-level `base_layer=0` serving + `V2HashRequestCoordinator` + pure-v2 tracker + wire handshake + dedup + OPFS persistence + service-worker streaming + HTTP file browser all shipped. Pure-v2-only magnets (`urn:btmh:`) now work end-to-end, keyed through `WireInfoHashHex` (first 20 bytes of SHA-256, libtorrent / qBittorrent / rqbit convention).
- **4 Tracker / Discovery Types** — WebSocket (browser + desktop, WebRTC signaling), HTTP/HTTPS, UDP (desktop), Local Service Discovery (BEP 14, multicast on the local subnet, desktop).
- **Web Seed Download** — HTTP range requests with multi-file piece assembly (BEP 17/19).
- **Persistent Storage** — Torrents and pieces persist in OPFS (browser) or filesystem (desktop). Survive page reloads. Resume downloading automatically.
- **Media Streaming with Seeking** — Service worker intercepts video/audio range requests and serves pieces on demand. `file.StreamURL`, `file.StreamTo(elem)`, `file.CreateReadStream()`. True seeking — pieces download as the video plays.
- **Service Worker** — `webtorrent-sw.js` ships with the library. Handles Cross-Origin-Isolation headers, Blazor loading, and torrent streaming via MessageChannel. One `<script>` tag and your app streams.
- **System.IO.Stream** — `file.CreateReadStream()` returns a seekable .NET `Stream` backed by torrent pieces. Use with any API that takes a Stream.
- **Random-Access Streaming** — Read any byte range from a torrent file as it downloads. Pieces download on demand. Perfect for ML model weight streaming.
- **Seeding** — Upload pieces to requesting peers with configurable rate limiting.
- **Inbound TCP Peer Listener** (desktop) — `WebTorrentClientOptions.TcpListenPort` accepts inbound BitTorrent peer-wire connections so mainline clients (qBittorrent, libtorrent, Transmission, rqbit) can dial in by IP+port and leech the torrents you're seeding. Routes inbound handshakes by info_hash to the matching torrent automatically. Set `AdvertiseTcpListenerToTrackers = true` and trackers put us in their compact peer list so peers find us automatically. See [Docs/tcp-listener.md](Docs/tcp-listener.md).
- **Pluggable piece-hash engine** — `IPieceHashEngine` interface with `BatchSha256` lets recheck-heavy workloads route piece verification through a GPU / batched implementation. Default `SystemCryptoPieceHashEngine` is byte-identical to 3.1.x; the GPU engine will ship as a separate package. See [Docs/hash-engine.md](Docs/hash-engine.md).
- **Bandwidth policy** — `BandwidthPolicy` enum (`Unlimited` / `Conservative` / `Metered` / `SeedingDisabled` / `Custom`) plus `WebTorrentClient.ApplyBandwidthPolicy(...)` runtime knob. Tell the client "be reasonable on a metered connection" without picking a number. See [Docs/bandwidth-policy.md](Docs/bandwidth-policy.md).
- **Speed Tracking** — Real-time download/upload bytes/sec per torrent.
- **AI Agent Communication** — BEP 46 DHT mutable items with Ed25519 signing via [SpawnDev.SpawnJS.Cryptography](https://github.com/LostBeard/SpawnDev.SpawnJS.Cryptography) 3.2.0+. AgentChannel pub/sub for shared AI state. `btpk` magnet URI support for mutable torrent subscriptions.
- **HuggingFace Integration** — Optional server extension that proxies HuggingFace model CDN with local caching and automatic torrent generation. Every browser that loads a model becomes a peer for the next; first request fills the cache, second request streams from peers. See [Docs/huggingface.md](Docs/huggingface.md) for the end-to-end pattern (server side + Blazor client side, with live `hub.spawndev.com` examples).
- **Custom Wire Extensions** — `UseExtension()` factory pattern (same as JS WebTorrent `wire.use()`). Build custom P2P protocols on top of the BitTorrent wire — distributed compute, AI agents, anything. Extensions negotiate via BEP 10.
- **.torrent Creation** — Create and parse .torrent files. Complete Bencode encoder/decoder. SHA-256 piece hashes (BEP 52 Phase 1) by default for stronger integrity on large ML model files; SHA-1 available via `HashAlgorithm = "SHA-1"` for v1 back-compat. `TorrentMetadata.PieceHashAlgorithm` surfaces which algorithm a parsed torrent uses.
- **BEP 52 v2 Torrents** — `TorrentCreatorOptions.MetaVersion = 2` produces proper BEP 52 v2 torrents with Merkle-tree piece verification (16 KiB leaves, SHA-256, per-level pad-hash propagation). Works for single-file (in-memory + streaming) and multi-file inputs. Parser surfaces `MetaVersion`, `V2InfoHash`, `FileRoots`, and `PieceLayers` on `TorrentMetadata`. Streaming path is bounded-memory - the incremental Merkle hasher uses ~128 KiB of state for a 1 GiB file.
- **Hybrid v1+v2 Torrents** — Add `Hybrid = true` to produce a single torrent with both the v1 SHA-1 piece list and the v2 Merkle tree in one info dict, yielding two valid infohashes (SHA-1 + SHA-256). Multi-file hybrid inserts BEP-52 pad files (`attr="p"`) between real files so both v1-only clients (qBittorrent pre-4.4, old libtorrent) and v2-aware clients see identically piece-aligned content.
- **BEP 52 v2 Magnet URIs** — `xt=urn:btmh:1220<sha256>` parsed into `Torrent.V2InfoHash`; hybrid magnets (both `urn:btih:` and `urn:btmh:`) fully supported.
- **Real Tests Everywhere** — 468+ shared test methods all running on BOTH browser and desktop runtimes via PlaywrightMultiTest (≈936 test executions per run) + libtorrent 2.0 external-interop fixtures + live WebRTC swarm integration. Every BEP tested, Ed25519 signing verified, official BEP 46 test vector validated, live WebRTC interop with JS WebTorrent peers, byte-level info-dict match against libtorrent's canonical output. BEP 52 v2 coverage includes Merkle hasher primitives, incremental streaming hasher, v2 torrent round-trip, hybrid v1+v2 round-trip, pad-file structure, v2 magnet URI, pure-v2 persist/restore, pure-v2 dedup, pure-v2 service-worker streaming. No mocks. Every test exercises real production code with real data. The NUnit-only SpawnDev.WebTorrent.Tests project was retired 2026-04-23 after all 258 of its tests migrated to `WebTorrentTestBase` partials so they run identically on browser + desktop.

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
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.Cryptography;
using SpawnDev.WebTorrent;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddSpawnJSRuntime();

// Cross-platform Ed25519 crypto for BEP 44/46 signing
builder.Services.AddPlatformCrypto();

// Persistent file system (OPFS in browser)
builder.Services.AddSingleton<IAsyncFS, AsyncFSFileSystemDirectoryHandle>();

// WebTorrent services — start with the app via IAsyncBackgroundService
builder.Services.AddSingleton<ServiceWorkerStreamHandler>();
builder.Services.AddSingleton<WebTorrentClient>();

await builder.Build().SpawnJSRunAsync();
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

Register custom wire-protocol extensions that participate in BEP 10 negotiation — same pattern as JS WebTorrent's `wire.use()`. The factory takes a `Wire` and returns an `IWireExtension`; one instance is created per peer connection, **before** the extended handshake so `OnExtendedHandshake` sees the peer's `m` dict.

```csharp
// Register on the client - applies to every torrent the client opens.
client.UseExtension(wire => new MyComputeExtension(wire));
```

Build extensions by implementing `IWireExtension`:

```csharp
public class MyComputeExtension : IWireExtension
{
    private readonly Wire _wire;
    private bool _peerHasIt;

    public MyComputeExtension(Wire wire) { _wire = wire; }

    public string Name => "sd_compute";

    public void OnHandshake(string infoHash, string peerId, WireExtensions extensions)
    {
        // Pre-extended-handshake hook (BEP 3 handshake just landed). Rarely needed.
    }

    public void OnExtendedHandshake(Dictionary<string, object> handshake)
    {
        // BEP 10 - the peer's `m` dict tells us which extensions it speaks.
        if (handshake.TryGetValue("m", out var mObj) && mObj is Dictionary<string, object> m)
            _peerHasIt = m.ContainsKey(Name);
    }

    public void OnMessage(byte[] buf)
    {
        // Inbound payload from the peer for this extension.
        ProcessIncoming(buf);
    }

    public Task SendComputeAsync(byte[] payload)
    {
        if (!_peerHasIt) return Task.CompletedTask;
        // Wire.Extended(name, payload) routes through the BEP 10 mapping the
        // peer advertised in its `m` dict. The bundled UtPexExtension uses
        // the same path: `await _wire.Extended("ut_pex", payload);`.
        return _wire.Extended(Name, payload);
    }

    private void ProcessIncoming(byte[] buf) { /* ... */ }
}
```

Use this for custom P2P protocols layered on top of the BitTorrent wire — distributed compute, AI agent communication, custom telemetry. The bundled `UtPexExtension` (BEP 11) and `UtMetadataExtension` (BEP 9) are reference implementations of the same interface.

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
| WPF Desktop Demo | Windows | Full torrent client UI, media player with seeking, drag-drop .torrent files, WebRTC P2P via SpawnDev.RTC (SipSorcery fork). |

Both demos connect to the same trackers and can P2P with each other.

## BEP Support

18 BitTorrent Enhancement Proposals implemented:

| BEP | Title | Desktop | Browser |
|-----|-------|---------|---------|
| 3 | BitTorrent Protocol | Yes | Yes |
| 5 | DHT (Kademlia) | Yes | Desktop only |
| 6 | Fast Extension | Yes | Yes |
| 9 | Magnet Links / ut_metadata | Yes | Yes |
| 10 | Extension Protocol | Yes | Yes |
| 11 | Peer Exchange (ut_pex) | Yes | Yes |
| 14 | Local Service Discovery (LSD) | Yes | Desktop only (UDP multicast) |
| 15 | UDP Tracker | Yes | N/A |
| 17 | HTTP Seeding | Yes | Yes |
| 19 | WebSeed | Yes | Yes |
| 20 | Peer ID Conventions | Yes | Yes |
| 23 | Compact Peer Lists | Yes | Yes |
| 27 | Private Torrents | Yes | Yes |
| 44 | DHT Storage | Yes | Desktop only |
| 46 | Mutable Items (AI Agents) | Yes | Yes |
| 48 | Tracker Scrape | Yes | Yes |
| 52 | v2 Torrents (SHA-256 / Merkle / hybrid) | Yes | Yes |
| 53 | Magnet File Selection | Yes | Yes |

See [Docs/bep-support.md](Docs/bep-support.md) for full details.

## Why This Exists

AI models are big. CDNs can't scale when every user downloads the same 2GB model. SpawnDev.WebTorrent turns every browser into a peer — the more users, the faster delivery. Built for [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML), the GPU-accelerated ML library for Blazor WebAssembly.

## Architecture

```
Browser Client                    Desktop Client
+----------------+                +-----------------+
| WebTorrentClient                | WebTorrentClient|
|                |                |                 |
| RtcPeer  <-----+----+--------->-+ RtcPeer         |
|  (SpawnJS     |    |           |  (SipSorcery    |
|   RTCPeer-     |    |           |   fork; same    |
|   Connection)  |    |           |   API)          |
|                |    |           | TcpPeer +/--    |
|                |    |           | TcpListener<--->| <- mainline peers
|                |    |           |                 |    (qBittorrent etc.)
| OPFS storage   |    |           | FileChunkStore  |
+----------------+    |           +-----------------+
        |             |                  |
        v             v                  v
+--------------------------------------------+
| hub.spawndev.com                           |
| SpawnDev.RTC.Server (WebSocket signaling)  |
| WebSeedServer (HTTP range fallback)        |
| HuggingFaceProxy (model CDN cache, SHA-256 |
|   piece hashes for integrity)              |
+--------------------------------------------+
```

## Documentation

### Protocol Documentation — `Research/`

The most thorough public reference for the WebTorrent + BitTorrent wire protocols, written and maintained inside this repo. Covers everything an implementer needs and is **directly useful to anyone writing a WebTorrent client in any language** — the WebTorrent community currently lacks a single complete written spec. Every behavior here has been observed against the JS reference (`webtorrent`, `bittorrent-tracker`, `bittorrent-protocol` npm packages) — not just paraphrased from BEPs.

| # | Document | Description |
|---|----------|-------------|
| 00 | [Research/00-README.md](Research/00-README.md) | Protocol research index |
| 01 | [Research/01-wire-protocol.md](Research/01-wire-protocol.md) | BitTorrent peer wire protocol (BEP 3 base messages, BEP 6 Fast Extension, BEP 10 extension framework, byte-exact handshake layout, message framing, choke/unchoke, request/piece/cancel, full state machine) |
| 02 | [Research/02-webtorrent-protocol.md](Research/02-webtorrent-protocol.md) | WebTorrent specifics: tracker WebSocket protocol, WebRTC data-channel setup, SDP offer/answer formats line-by-line, browser vs SipSorcery SDP differences, complete session walkthrough |
| 03 | [Research/03-extension-protocols.md](Research/03-extension-protocols.md) | BEP 9 ut_metadata, BEP 11 ut_pex, lt_donthave - extension handshake, message types, peer inclusion rules |
| 04 | [Research/04-tracker-protocols.md](Research/04-tracker-protocols.md) | HTTP, UDP, and WebSocket tracker protocols. Includes a verified-against-JS-reference behavior section for the WebSocket tracker (announce response shape, answer-relay no-response rule, stopped-event response with counts, offer-forwarding selection algorithm, scrape, frame-size limits) |
| 05 | [Research/05-dht-protocol.md](Research/05-dht-protocol.md) | DHT (BEP 5) Kademlia + KRPC, mutable items (BEP 44/46), Ed25519 signing |
| 06 | [Research/06-web-seeds.md](Research/06-web-seeds.md) | BEP 17 (HTTP seeding) and BEP 19 (web seeding via getright-style HTTP range requests) |
| 07 | [Research/07-lifecycle.md](Research/07-lifecycle.md) | Master lifecycle: full order of operations from announce through download to seed |
| 08 | [Research/08-sipsorcery-interop.md](Research/08-sipsorcery-interop.md) | SipSorcery / browser WebRTC interop analysis |
| 09 | [Research/09-sipsorcery-dtls-analysis.md](Research/09-sipsorcery-dtls-analysis.md) | Why we fork SipSorcery for DTLS/SRTP — proven BouncyCastle DTLS stack vs upstream's SharpSRTP rewrite |

The `tracker-debug/` directory ships parity harnesses (`verify-tracker-parity.mjs`, `verify-offer-flow.mjs`, `verify-offer-flow-local.mjs`) that compare SpawnDev's tracker behavior to the JS reference frame-by-frame; run before any change to tracker wire code.

### API & Implementation

| Doc | Description |
|-----|-------------|
| [API Reference](Docs/api.md) | Full API surface: `WebTorrentClient`, `Torrent`, `File`, `TorrentCreator`, `AgentChannel`, wire extensions |
| [BEP Support](Docs/bep-support.md) | BitTorrent Enhancement Proposal implementation status |
| [BEP 52 v2 Overview](Docs/bep52.md) | SHA-256 + Merkle-tree torrents, hybrid v1+v2, wire-protocol details |
| [BEP 52 v2 Walkthrough](Docs/bep52-example.md) | Code samples for creating, parsing, and verifying v2 torrents |
| [TCP Peer Listener](Docs/tcp-listener.md) | `WebTorrentClientOptions.TcpListenPort` + `AdvertiseTcpListenerToTrackers` for accepting inbound BitTorrent peer-wire connections from mainline clients |
| [Hash Engine](Docs/hash-engine.md) | `IPieceHashEngine` slot-in for piece verification — default `SystemCryptoPieceHashEngine`, GPU-batched implementations land via a separate package |
| [Bandwidth Policy](Docs/bandwidth-policy.md) | `BandwidthPolicy` enum + `ApplyBandwidthPolicy(...)` for upload-throttle intent (Unlimited / Conservative / Metered / SeedingDisabled / Custom) |
| [Service Worker](Docs/service-worker.md) | `webtorrent-sw.js` deep dive — Cross-Origin-Isolation headers, `/webtorrent/{hash}/{fileIdx}` streaming, MessageChannel protocol, video seeking |
| [HuggingFace Model Delivery](Docs/huggingface.md) | End-to-end ML model delivery via P2P. Server side (proxy mount + standalone hub options) AND client side (Blazor Quick Start, magnet → AddAsync → stream pattern). Live `hub.spawndev.com` examples. |
| [qBittorrent Interop Testing](Docs/qbittorrent-interop.md) | How to run the `interop_test/` scripts against a local qBittorrent Web UI. Static binary-compat + live-swarm both directions + JS WebTorrent live-swarm all PASSING. |
| [Protocol Reference Captures](Docs/protocol-reference/) | Raw protocol captures from instrumented JS WebTorrent sessions (used to build the synthesized Research/ docs) |

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
