# Architecture

## Project Structure

```
SpawnDev.WebTorrent/
├── SpawnDev.WebTorrent/              # Client library (NuGet 1.1.0)
│   ├── Bencode/                      # Bencode encoder/decoder
│   ├── Discovery/                    # Peer discovery (4 sources)
│   │   ├── WebSocketTrackerClient    # WebSocket JSON tracker + WebRTC relay
│   │   ├── HttpTrackerClient         # HTTP/HTTPS announce
│   │   ├── UdpTrackerClient          # BEP 15 binary UDP
│   │   └── DhtDiscovery              # BEP 5 Kademlia + KRPC
│   ├── ModelDelivery/                # ML model streaming integration
│   ├── Storage/                      # Chunk storage (3 backends)
│   │   ├── MemoryChunkStore          # In-memory (default fallback)
│   │   ├── FileChunkStore            # Filesystem (desktop)
│   │   └── AsyncFSChunkStore         # OPFS persistent (browser)
│   ├── Torrent/                      # Torrent management
│   │   ├── TorrentSwarm              # Full peer lifecycle, choke rotation, endgame
│   │   ├── PieceManager              # Block tracking, SHA-1 verify, rarest-first
│   │   ├── DownloadCoordinator       # Peer requests + web seed fallback
│   │   ├── TorrentFileStream         # Random-access reads, MIME detection, streaming
│   │   ├── TorrentParser             # .torrent + magnet URI parsing
│   │   └── TorrentCreator            # .torrent file creation
│   ├── Transports/                   # Network transports (4 types)
│   │   ├── IWebRtcTransport          # Shared interface for browser + desktop
│   │   ├── WebRtcTransport           # Browser (SpawnDev.BlazorJS RTCPeerConnection)
│   │   ├── SipSorceryWebRtcTransport # Desktop (SIPSorcery 10.0.3)
│   │   ├── TcpTransport              # Desktop TCP
│   │   └── WebSeedConnection         # HTTP range requests (multi-file assembly)
│   ├── Wire/                         # BitTorrent wire protocol
│   │   ├── WireProtocol              # BEP 3 + BEP 6 messages, read loop
│   │   ├── ExtensionManager          # BEP 10 handshake + routing
│   │   ├── UtMetadataExtension       # BEP 9 metadata exchange
│   │   └── UtPexExtension            # BEP 11 peer exchange
│   ├── PeerCoordinator               # Tracker → WebRTC signaling → wire protocol
│   ├── TorrentHttpServer             # HTTP server with range requests
│   ├── RateLimiter                   # Token bucket throttling
│   └── WebTorrentClient              # Main client API
│
├── SpawnDev.WebTorrent.Server/       # Server library (NuGet 1.0.0)
│   ├── TorrentTracker                # WebSocket tracker with signaling relay
│   ├── WebSeedServer                 # HTTP range request piece server
│   └── WebTorrentServerExtensions    # ASP.NET endpoint registration
│
├── SpawnDev.WebTorrent.Server.HuggingFace/
│   └── HuggingFaceProxy              # CDN proxy, caching, torrent generation
│
├── SpawnDev.WebTorrent.ServerApp/    # Production deployment (hub.spawndev.com)
├── SpawnDev.WebTorrent.Demo/         # Blazor WASM — qBittorrent v5 UI
├── SpawnDev.WebTorrent.Demo.Shared/  # 161 test methods (188 Playwright tests)
├── SpawnDev.WebTorrent.WpfDemo/      # WPF Desktop — qBittorrent v5 UI
└── PlaywrightMultiTest/              # Browser test runner
```

## Data Flow

### Adding a Torrent (Magnet URI)

```
1. client.AddAsync("magnet:?xt=urn:btih:...")
   → Parse info hash from magnet URI
   → Create TorrentSwarm (no metadata yet)
   → Create chunk store (OPFS/FileSystem/Memory)

2. Fetch .torrent from xs= URL (if present)
   → HTTP GET → parse bencode → SetMetadata()
   → Create PieceManager + DownloadCoordinator
   → Create TorrentFileStream per file

3. Connect trackers (parallel)
   → WebSocket: JSON announce + WebRTC signaling
   → HTTP: URL-encoded announce + compact peers
   → UDP: binary connect + announce
   → DHT: Kademlia lookup + announce

4. Peer connection (via PeerCoordinator)
   → Tracker discovers peer → create RTCPeerConnection
   → Exchange SDP offer/answer through tracker relay
   → ICE gathers candidates → data channel opens
   → BitTorrent handshake (68 bytes)
   → BEP 10 extension handshake
   → Bitfield exchange

5. Piece download
   → DownloadCoordinator selects pieces (rarest-first/sequential)
   → Request blocks from peers (16KB each)
   → Web seed fallback when no peers available
   → SHA-1 verify each piece → store in ChunkStore
   → Endgame mode: last 5 pieces from ALL peers

6. Seeding
   → Respond to Request messages with stored piece data
   → Upload rate limiting via token bucket
   → Choke/unchoke rotation (10s/30s optimistic)
```

### Transport Matrix

| Transport | Platform | Protocol | Use Case |
|-----------|----------|----------|----------|
| WebRtcTransport | Browser | RTCDataChannel | Browser-to-browser P2P |
| SipSorceryWebRtcTransport | Desktop | RTCDataChannel | Desktop-to-browser P2P |
| TcpTransport | Desktop | TCP sockets | Desktop-to-desktop P2P |
| WebSeedConnection | Both | HTTP Range | CDN fallback |

### Storage Matrix

| Store | Platform | Persistence | Use Case |
|-------|----------|-------------|----------|
| AsyncFSChunkStore | Browser | OPFS (permanent) | Downloaded pieces survive page reload |
| FileChunkStore | Desktop | Filesystem | Downloaded pieces in temp directory |
| MemoryChunkStore | Both | None | Testing, fallback |

## BEP Implementation (13)

BEP 3, 5, 6, 9, 10, 11, 15, 17, 19, 20, 23, 27, 53

## Port Assignments

| Port | Service |
|------|---------|
| 5560 | ServerApp HTTPS |
| 5561 | ServerApp HTTP |
| 5562 | PlaywrightMultiTest |
| 18770 | WPF TorrentHttpServer |

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| SpawnDev.BlazorJS | 3.5.0 | Browser WebRTC + OPFS wrappers |
| SpawnDev.AsyncFileSystem | 1.0.0 | Cross-platform persistent storage |
| SIPSorcery | 10.0.3 | Desktop WebRTC data channels |

## Test Coverage

188 Playwright tests (185 pass, 3 skip for desktop-only):
- Bencode encoding/decoding (including edge cases)
- Torrent creation and parsing (single + multi-file)
- Magnet URI parsing (including BEP 53)
- Wire protocol message framing (all message types)
- BEP 6 Fast Extension (HaveAll/None/Suggest/Reject/AllowedFast)
- Extension handshake + ut_metadata + ut_pex
- PieceManager (selection, verification, block tracking, edge cases)
- ChunkStore (put/get/remove/clear, partial reads, edge cases)
- WebRTC transport construction
- Tracker connections (WebSocket, HTTP, UDP, DHT)
- Full download pipeline (web seed → pieces → verify)
- P2P integration (seed → connect → transfer → verify byte-for-byte)
- Controlled swarm (two clients, mock loopback)
- Pause/resume during download
- Browse files before download (paused + select)
- Client lifecycle (dispose, rapid add/remove)
- File streaming (IAsyncEnumerable, blob, range)
- Media playback (blob URL creation)
- Settings (rate limits, tracker list)
- 25 MIME types verified
