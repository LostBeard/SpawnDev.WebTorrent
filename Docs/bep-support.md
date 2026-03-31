# BEP (BitTorrent Enhancement Proposal) Support

Status of BEP implementation in SpawnDev.WebTorrent v2.1.0.

## Implemented

| BEP | Title | Desktop | Browser | Notes |
|-----|-------|---------|---------|-------|
| [3](http://bittorrent.org/beps/bep_0003.html) | The BitTorrent Protocol | Yes | Yes | Full wire protocol, piece exchange, SHA-1 and SHA-256 verification |
| [5](http://bittorrent.org/beps/bep_0005.html) | DHT Protocol | Yes | Desktop only | Kademlia KRPC over UDP. No UDP in browser. |
| [6](http://bittorrent.org/beps/bep_0006.html) | Fast Extension | Yes | Yes | HaveAll, HaveNone, SuggestPiece, RejectRequest, AllowedFast |
| [9](http://bittorrent.org/beps/bep_0009.html) | Extension for Peers to Send Metadata | Yes | Yes | ut_metadata: request/data/reject, 16KB pieces |
| [10](http://bittorrent.org/beps/bep_0010.html) | Extension Protocol | Yes | Yes | Handshake negotiation, message routing, custom extensions via `UseExtension()` factory |
| [11](http://bittorrent.org/beps/bep_0011.html) | Peer Exchange (PEX) | Yes | Yes | ut_pex: compact IPv4 peer list parsing (works over WebRTC wire protocol) |
| [15](http://bittorrent.org/beps/bep_0015.html) | UDP Tracker Protocol | Yes | N/A | Connect, announce, compact peer lists. No UDP in browser. |
| [17](http://bittorrent.org/beps/bep_0017.html) | HTTP Seeding (Hoffman) | Yes | Yes | HTTP range request web seeds |
| [19](http://bittorrent.org/beps/bep_0019.html) | WebSeed (GetRight style) | Yes | Yes | Multi-file piece assembly, path-safe URL encoding |
| [20](http://bittorrent.org/beps/bep_0020.html) | Peer ID Conventions | Yes | Yes | Azureus-style: `-SD0210-` (see [peer-id.md](peer-id.md)) |
| [23](http://bittorrent.org/beps/bep_0023.html) | Tracker Returns Compact Peer Lists | Yes | Yes | Via UDP and HTTP tracker compact format |
| [27](http://bittorrent.org/beps/bep_0027.html) | Private Torrents | Yes | Yes | DHT/PEX peers rejected, only tracker peers accepted |
| [44](http://bittorrent.org/beps/bep_0044.html) | Storing Arbitrary Data in DHT | Yes | Desktop only | Requires DHT (UDP). |
| [46](http://bittorrent.org/beps/bep_0046.html) | Updating Torrents via DHT Mutable Items | Yes | Yes | ECDSA-P256 signed (WebCrypto native), AgentChannel pub/sub, AI shared state |
| [53](http://bittorrent.org/beps/bep_0053.html) | Magnet URI — Select Specific Files | Yes | Yes | `so=` parameter parsed, `SelectedFileIndices` property |

## Piece Verification

| Algorithm | Create | Verify | Auto-detect |
|-----------|--------|--------|-------------|
| SHA-1 (20 bytes) | Yes | Yes | Default for legacy compatibility |
| SHA-256 (32 bytes) | Yes | Yes | Default for new torrents. Auto-detected from piece hash size. |

Verification uses `IPortableCrypto` when available:
- **Browser:** SubtleCrypto (native C++ — fast)
- **Desktop:** System.Security.Cryptography

## Tracker Support

| Type | Desktop | Browser | Protocol |
|------|---------|---------|----------|
| WebSocket (wss://) | Yes | Yes | JSON signaling with WebRTC offer/answer relay |
| HTTP/HTTPS | Yes | Yes | URL-encoded announce, compact/non-compact peer lists |
| UDP | Yes | N/A | BEP 15 binary protocol (connect/announce/scrape) |

## WebRTC Transport

| Platform | Library | Notes |
|----------|---------|-------|
| Browser | SpawnDev.BlazorJS RTCPeerConnection | Full signaling via tracker relay |
| Desktop | SIPSorcery 10.0.3 RTCPeerConnection | Same signaling protocol, cross-platform P2P |

Both share the `IWebRtcTransport` interface and work through the same `PeerCoordinator` signaling flow.

## Wire Extensions

Custom wire protocol extensions via `UseExtension()` factory pattern (same as JS WebTorrent `wire.use()`):

| Level | Method | Scope |
|-------|--------|-------|
| Client | `client.UseExtension(factory)` | All swarms, all peers |
| Swarm | `swarm.UseExtension(factory)` | All peers on this torrent |

Extensions are created per-peer, registered before BEP 10 handshake. See [wire-extensions.md](wire-extensions.md) for full guide.

## Additional Features

| Feature | Desktop | Browser | Notes |
|---------|---------|---------|-------|
| SHA-256 piece hashing | Yes | Yes | Default for new torrents via `TorrentCreatorOptions.HashAlgorithm` |
| Web seed HTTP range | Yes | Yes | Multi-file piece assembly |
| Torrent creation from URL | Yes | Yes | `TorrentCreator.CreateFromUrlAsync()` — streams + hashes, adds web seed |
| Custom wire extensions | Yes | Yes | `UseExtension()` factory — BEP 10 negotiated |
| Upload rate limiting | Yes | Yes | Token bucket via RateLimiter |
| Download rate limiting | Yes | Yes | Token bucket via RateLimiter |
| OPFS persistent storage | N/A | Yes | Via SpawnDev.AsyncFileSystem |
| FileSystem storage | Yes | Yes | FileChunkStore (desktop) / AsyncFSChunkStore (browser OPFS) |
| Keep-alive timer | Yes | Yes | 60-second interval per peer |
| Choke/unchoke rotation | Yes | Yes | 10s rechoke, 30s optimistic unchoke |
| Endgame mode | Yes | Yes | Request from all peers when <=5 pieces remain |
| Speed tracking | Yes | Yes | Real-time bytes/sec per torrent |
| Seeding | Yes | Yes | Respond to Request with Piece data |
| Media playback | N/A | Yes | Video/audio/image via blob URL with seeking |
| AI Agent Communication | Yes | Yes | AgentChannel pub/sub, SwarmCompute task distribution |

## Not Yet Implemented

| BEP | Title | Priority | Notes |
|-----|-------|----------|-------|
| [14](http://bittorrent.org/beps/bep_0014.html) | Local Service Discovery | Low | Desktop only, UDP broadcast |
| [29](http://bittorrent.org/beps/bep_0029.html) | uTorrent Transport Protocol (uTP) | Low | UDP-based, desktop only |
| [48](http://bittorrent.org/beps/bep_0048.html) | Tracker Scrape | Low | Get counts without announcing |
| [52](http://bittorrent.org/beps/bep_0052.html) | BitTorrent Protocol v2 | Future | Full Merkle tree support |

## Test Coverage

461+ tests (all pass, 10 skip for desktop-only) covering all 15 implemented BEPs, real WebRTC P2P piece exchange, web seed streaming, torrent creation from URLs, SHA-256 verification, the full download pipeline, controlled swarm, AI agent communication, parser round-trips, storage backends (Memory, File, OPFS), PieceManager state machine, wire extensions, swarm properties, and cross-platform functionality. Tested via PlaywrightMultiTest against real-world torrents (Big Buck Bunny, Sintel) and live hub.spawndev.com tracker.
