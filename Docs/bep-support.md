# BEP (BitTorrent Enhancement Proposal) Support

Status of BEP implementation in SpawnDev.WebTorrent.

## Implemented

| BEP | Title | Status | Notes |
|-----|-------|--------|-------|
| [BEP 3](http://bittorrent.org/beps/bep_0003.html) | The BitTorrent Protocol | Done | Core wire protocol, piece exchange, SHA-1 verification |
| [BEP 10](http://bittorrent.org/beps/bep_0010.html) | Extension Protocol | Done | Extension handshake, message routing |
| [BEP 17](http://bittorrent.org/beps/bep_0017.html) | HTTP Seeding (Hoffman) | Done | Web seed via HTTP range requests |
| [BEP 19](http://bittorrent.org/beps/bep_0019.html) | WebSeed (GetRight) | Done | Alternative web seed format |
| [BEP 20](http://bittorrent.org/beps/bep_0020.html) | Peer ID Conventions | Done | Azureus-style: `-SD0110-` (see [peer-id.md](peer-id.md)) |

## In Progress

| BEP | Title | Status | Notes |
|-----|-------|--------|-------|
| [BEP 9](http://bittorrent.org/beps/bep_0009.html) | Extension for Peers to Send Metadata | Partial | ut_metadata extension framework, needs full metadata assembly |
| [BEP 11](http://bittorrent.org/beps/bep_0011.html) | Peer Exchange (PEX) | Partial | ut_pex extension framework |

## Planned

| BEP | Title | Priority | Notes |
|-----|-------|----------|-------|
| [BEP 5](http://bittorrent.org/beps/bep_0005.html) | DHT Protocol | Post-1.1 | Decentralized peer discovery |
| [BEP 6](http://bittorrent.org/beps/bep_0006.html) | Fast Extension | Post-1.1 | Have All/None, Reject, Allowed Fast |
| [BEP 29](http://bittorrent.org/beps/bep_0029.html) | uTorrent Transport Protocol | N/A | Not needed for WebRTC transport |

## Non-Standard Extensions

| Feature | Description |
|---------|-------------|
| WebRTC Data Channels | Browser-to-browser P2P via RTCPeerConnection (WebTorrent protocol) |
| WebSocket Tracker | JSON-based tracker with WebRTC signaling relay (WebTorrent protocol) |
| HuggingFace Proxy | Server extension for ML model delivery via torrent |
| Random-Access Streaming | Byte-range reads from in-progress downloads for ML model weights |
