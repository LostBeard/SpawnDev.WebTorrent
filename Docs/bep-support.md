# BEP (BitTorrent Enhancement Proposal) Support

Status of BEP implementation in SpawnDev.WebTorrent v1.1.0.

## Implemented

| BEP | Title | Desktop | Browser | Notes |
|-----|-------|---------|---------|-------|
| [3](http://bittorrent.org/beps/bep_0003.html) | The BitTorrent Protocol | Yes | Yes | Full wire protocol, piece exchange, SHA-1 verification |
| [5](http://bittorrent.org/beps/bep_0005.html) | DHT Protocol | Yes | Via relay | Kademlia KRPC over UDP (desktop), WebSocket tracker relay (browser) |
| [6](http://bittorrent.org/beps/bep_0006.html) | Fast Extension | Yes | Yes | HaveAll, HaveNone, SuggestPiece, RejectRequest, AllowedFast |
| [9](http://bittorrent.org/beps/bep_0009.html) | Extension for Peers to Send Metadata | Yes | Yes | ut_metadata: request/data/reject, 16KB pieces, SHA-1 verify |
| [10](http://bittorrent.org/beps/bep_0010.html) | Extension Protocol | Yes | Yes | Handshake negotiation, message routing, extension framework |
| [11](http://bittorrent.org/beps/bep_0011.html) | Peer Exchange (PEX) | Yes | Yes | ut_pex: compact IPv4 peer list parsing (works over WebRTC wire protocol) |
| [15](http://bittorrent.org/beps/bep_0015.html) | UDP Tracker Protocol | Yes | N/A | Connect, announce, compact peer lists. No UDP in browser. |
| [17](http://bittorrent.org/beps/bep_0017.html) | HTTP Seeding (Hoffman) | Yes | Yes | HTTP range request web seeds |
| [19](http://bittorrent.org/beps/bep_0019.html) | WebSeed (GetRight style) | Yes | Yes | Multi-file piece assembly, path-safe URL encoding |
| [20](http://bittorrent.org/beps/bep_0020.html) | Peer ID Conventions | Yes | Yes | Azureus-style: `-SD0110-` (see [peer-id.md](peer-id.md)) |
| [23](http://bittorrent.org/beps/bep_0023.html) | Tracker Returns Compact Peer Lists | Yes | Yes | Via UDP and HTTP tracker compact format |
| [27](http://bittorrent.org/beps/bep_0027.html) | Private Torrents | Yes | Yes | DHT/PEX peers rejected, only tracker peers accepted |
| [44](http://bittorrent.org/beps/bep_0044.html) | Storing Arbitrary Data in DHT | Yes | Yes* | Via DHT (desktop) or WebSocket relay (browser) |
| [46](http://bittorrent.org/beps/bep_0046.html) | Updating Torrents via DHT Mutable Items | Yes | Yes | ECDSA-P256 signed (WebCrypto native), AgentChannel pub/sub, AI shared state |
| [53](http://bittorrent.org/beps/bep_0053.html) | Magnet URI — Select Specific Files | Yes | Yes | `so=` parameter parsed, `SelectedFileIndices` property |

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

## Additional Features

| Feature | Desktop | Browser | Notes |
|---------|---------|---------|-------|
| Web seed HTTP range | Yes | Yes | Multi-file piece assembly |
| Upload rate limiting | Yes | Yes | Token bucket via RateLimiter |
| Download rate limiting | Yes | Yes | Token bucket via RateLimiter |
| OPFS persistent storage | N/A | Yes | Via SpawnDev.AsyncFileSystem |
| FileSystem storage | Yes | N/A | FileChunkStore in temp directory |
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
| [52](http://bittorrent.org/beps/bep_0052.html) | BitTorrent Protocol v2 | Future | SHA-256, Merkle trees |

## Test Coverage

244 tests (236 pass, 8 skip for desktop-only) covering all 15 implemented BEPs, the full download pipeline, P2P integration, controlled swarm, AI agent communication, and cross-platform functionality. Tested via PlaywrightMultiTest against real-world torrents (Big Buck Bunny, Sintel).
