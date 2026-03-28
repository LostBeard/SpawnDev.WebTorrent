# BEP (BitTorrent Enhancement Proposal) Support

Status of BEP implementation in SpawnDev.WebTorrent.

## Implemented

| BEP | Title | Desktop | Browser | Notes |
|-----|-------|---------|---------|-------|
| [3](http://bittorrent.org/beps/bep_0003.html) | The BitTorrent Protocol | Yes | Yes | Full wire protocol, piece exchange, SHA-1 verification |
| [6](http://bittorrent.org/beps/bep_0006.html) | Fast Extension | Yes | Yes | HaveAll, HaveNone, SuggestPiece, RejectRequest, AllowedFast |
| [9](http://bittorrent.org/beps/bep_0009.html) | Extension for Peers to Send Metadata | Yes | Yes | ut_metadata: request/data/reject, 16KB pieces, SHA-1 verify |
| [10](http://bittorrent.org/beps/bep_0010.html) | Extension Protocol | Yes | Yes | Handshake negotiation, message routing, extension framework |
| [11](http://bittorrent.org/beps/bep_0011.html) | Peer Exchange (PEX) | Yes | N/A | ut_pex: compact IPv4 peer list parsing (6-byte format) |
| [15](http://bittorrent.org/beps/bep_0015.html) | UDP Tracker Protocol | Yes | N/A | Connect, announce, compact peer lists. No UDP in browser. |
| [17](http://bittorrent.org/beps/bep_0017.html) | HTTP Seeding (Hoffman) | Yes | Yes | HTTP range request web seeds |
| [19](http://bittorrent.org/beps/bep_0019.html) | WebSeed (GetRight style) | Yes | Yes | Multi-file piece assembly, path-safe URL encoding |
| [20](http://bittorrent.org/beps/bep_0020.html) | Peer ID Conventions | Yes | Yes | Azureus-style: `-SD0110-` (see [peer-id.md](peer-id.md)) |
| [23](http://bittorrent.org/beps/bep_0023.html) | Tracker Returns Compact Peer Lists | Yes | Yes | Via UDP tracker compact format |
| [27](http://bittorrent.org/beps/bep_0027.html) | Private Torrents | Yes | Yes | DHT/PEX peers rejected, only tracker peers accepted |
| [53](http://bittorrent.org/beps/bep_0053.html) | Magnet URI — Select Specific Files | Yes | Yes | `so=` parameter parsed, `SelectedFileIndices` property |

## WebTorrent Protocol Extensions

| Feature | Desktop | Browser | Notes |
|---------|---------|---------|-------|
| WebRTC Data Channels | Yes (SIPSorcery) | Yes (SpawnDev.BlazorJS) | Browser-to-browser and desktop-to-browser P2P |
| WebSocket Tracker | Yes | Yes | JSON signaling with WebRTC offer/answer relay |
| Web Seed HTTP Range | Yes | Yes | Multi-file torrents, correct byte range mapping |

## Not Yet Implemented

| BEP | Title | Priority | Notes |
|-----|-------|----------|-------|
| [5](http://bittorrent.org/beps/bep_0005.html) | DHT Protocol | Medium | DhtDiscovery stub exists, needs Kademlia routing |
| [14](http://bittorrent.org/beps/bep_0014.html) | Local Service Discovery | Low | Desktop only. UDP broadcast. |
| [29](http://bittorrent.org/beps/bep_0029.html) | uTorrent Transport Protocol (uTP) | Low | UDP-based, desktop only |
| [48](http://bittorrent.org/beps/bep_0048.html) | Tracker Scrape | Low | Get seeder/leecher counts without announcing |
| [7](http://bittorrent.org/beps/bep_0007.html) | IPv6 Tracker Extension | Low | |
| [32](http://bittorrent.org/beps/bep_0032.html) | DHT Extensions for IPv6 | Low | Requires BEP 5 first |
| [52](http://bittorrent.org/beps/bep_0052.html) | BitTorrent Protocol v2 | Future | SHA-256, Merkle trees |

## Test Coverage

140 unit tests covering all implemented BEPs. Every protocol message format, every extension handler, and every download pipeline path is tested through PlaywrightMultiTest (browser) and verified against real-world torrents (Big Buck Bunny, Sintel, etc.).
