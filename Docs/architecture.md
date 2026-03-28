# Architecture

## Project Structure

```
SpawnDev.WebTorrent/
├── SpawnDev.WebTorrent/              # Client library (NuGet package)
│   ├── Bencode/                      # Bencode encoder/decoder
│   ├── Discovery/                    # Peer discovery (trackers, DHT)
│   ├── ModelDelivery/                # ML model streaming integration
│   ├── Storage/                      # Chunk storage (memory, file)
│   ├── Torrent/                      # Torrent management, piece verification
│   ├── Transports/                   # Network transports (TCP, WebRTC, WebSeed)
│   ├── Wire/                         # BitTorrent wire protocol + extensions
│   ├── PeerCoordinator.cs            # Glue: tracker -> WebRTC -> wire protocol
│   └── WebTorrentClient.cs           # Main client API
│
├── SpawnDev.WebTorrent.Server/       # Server library (NuGet package)
│   ├── TorrentTracker.cs             # WebSocket tracker with signaling relay
│   ├── WebSeedServer.cs              # HTTP range request piece server
│   └── WebTorrentServerExtensions.cs # ASP.NET endpoint registration
│
├── SpawnDev.WebTorrent.Server.HuggingFace/  # HF proxy extension
│   └── HuggingFaceProxy.cs           # CDN proxy, caching, torrent generation
│
├── SpawnDev.WebTorrent.ServerApp/    # Production deployment (hub.spawndev.com)
├── SpawnDev.WebTorrent.Demo/         # Blazor WASM demo + GitHub Pages
├── SpawnDev.WebTorrent.Demo.Shared/  # Shared unit tests (67 tests)
└── PlaywrightMultiTest/              # Browser test runner
```

## Data Flow

### Browser Client Joining a Swarm

```
1. Client connects to tracker via WebSocket
   WebSocketTrackerClient -> wss://hub.spawndev.com:44365/announce

2. Client announces with info_hash + peer_id
   Tracker responds with peer list (seeders/leechers)

3. For each discovered peer:
   a. Create RTCPeerConnection with STUN servers
   b. Create data channel + SDP offer
   c. Send offer through tracker (signaling relay)
   d. Receive answer through tracker
   e. ICE connectivity establishes direct P2P path

4. Data channel opens -> BitTorrent wire protocol handshake (68 bytes)
   - Protocol string: "BitTorrent protocol"
   - Reserved bytes with BEP 10 extension flag
   - Info hash (20 bytes) + Peer ID (20 bytes)

5. Piece exchange begins
   - Request/Piece messages over the data channel
   - SHA-1 verification per piece
   - Rarest-first piece selection
```

### Web Seed Fallback

When no peers are available or for initial seeding:

```
1. Parse web seed URLs from magnet URI (&ws=) or .torrent (url-list)
2. HTTP GET with Range header for specific byte ranges
3. Map byte ranges to torrent pieces
4. SHA-1 verify received data
5. Store verified pieces in chunk store
```

## Transport Layer

All transports implement `ITransport` / `IConnection`:

| Transport | Platform | Protocol | Use Case |
|-----------|----------|----------|----------|
| `WebRtcTransport` | Browser | RTCDataChannel | Browser-to-browser P2P |
| `TcpTransport` | Desktop | TCP sockets | Desktop-to-desktop P2P |
| `WebSeedConnection` | Both | HTTP Range | Server-to-client fallback |

### WebRTC Transport (Browser)

Uses SpawnDev.BlazorJS wrappers for the browser WebRTC API:
- `RTCPeerConnection` — Peer connection with ICE/STUN
- `RTCDataChannel` — Binary data channel (binaryType = "arraybuffer")
- ICE gathering completes before offer/answer exchange (bundled candidates)
- Data arrives as `ArrayBuffer` -> `Uint8Array` -> `byte[]`

## Wire Protocol (BEP 3 + BEP 10)

Standard BitTorrent wire protocol over any transport:

| Message | ID | Payload |
|---------|----|---------|
| Choke | 0 | — |
| Unchoke | 1 | — |
| Interested | 2 | — |
| NotInterested | 3 | — |
| Have | 4 | piece index (4 bytes) |
| Bitfield | 5 | bitfield |
| Request | 6 | index + offset + length (12 bytes) |
| Piece | 7 | index + offset + data |
| Cancel | 8 | index + offset + length (12 bytes) |
| Extended | 20 | BEP 10 extension messages |

Extensions supported:
- **ut_metadata** (BEP 9) — Metadata exchange for magnet URIs
- **ut_pex** (BEP 11) — Peer exchange

## Storage

| Store | Platform | Persistence |
|-------|----------|-------------|
| `MemoryChunkStore` | Both | None (lost on page reload) |
| `FileChunkStore` | Desktop | Disk |
| OPFS (planned) | Browser | Persistent via Origin Private File System |

## Port Assignments

| Port | Service |
|------|---------|
| 5560 | ServerApp HTTPS |
| 5561 | ServerApp HTTP |
| 5562 | PlaywrightMultiTest |
