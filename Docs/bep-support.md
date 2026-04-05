# BEP (BitTorrent Enhancement Proposal) Support

Status of BEP implementation in SpawnDev.WebTorrent _Alt.

## Implemented

| BEP | Title | Desktop | Browser | Tests | Notes |
|-----|-------|---------|---------|-------|-------|
| [3](http://bittorrent.org/beps/bep_0003.html) | The BitTorrent Protocol | Yes | Yes | 12 wire + 10 download + 8 seed + 8 file | Full wire protocol, piece exchange, SHA-1 and SHA-256 verification |
| [5](http://bittorrent.org/beps/bep_0005.html) | DHT Protocol | Yes | No | 5 | Kademlia KRPC over UDP. Browser has no UDP sockets — hard platform constraint. |
| [6](http://bittorrent.org/beps/bep_0006.html) | Fast Extension | Yes | Yes | 2 | HaveAll, HaveNone, SuggestPiece, RejectRequest, AllowedFast |
| [9](http://bittorrent.org/beps/bep_0009.html) | Extension for Peers to Send Metadata | Yes | Yes | 2 | ut_metadata: request/data/reject, 16KB pieces |
| [10](http://bittorrent.org/beps/bep_0010.html) | Extension Protocol | Yes | Yes | 1 | Handshake negotiation, message routing, custom extensions via `UseExtension()` |
| [11](http://bittorrent.org/beps/bep_0011.html) | Peer Exchange (PEX) | Yes | Yes | 5 | ut_pex: compact IPv4/IPv6 peer lists, rate limiting, dedup, 50-peer cap |
| [14](http://bittorrent.org/beps/bep_0014.html) | Local Service Discovery | Yes | No | — | UDP multicast 239.192.152.143:6771. Browser has no UDP — hard platform constraint. |
| [15](http://bittorrent.org/beps/bep_0015.html) | UDP Tracker Protocol | Yes | No | 3 | Connect, announce, exponential backoff. Browser has no UDP — hard platform constraint. |
| [17](http://bittorrent.org/beps/bep_0017.html) | HTTP Seeding (Hoffman) | Yes | Yes | 1 | HTTP range request web seeds |
| [19](http://bittorrent.org/beps/bep_0019.html) | WebSeed (GetRight style) | Yes | Yes | 1 | Multi-file piece assembly, path-safe URL encoding |
| [20](http://bittorrent.org/beps/bep_0020.html) | Peer ID Conventions | Yes | Yes | 2 | Azureus-style: `-SD{ver}-` + random suffix |
| [23](http://bittorrent.org/beps/bep_0023.html) | Tracker Returns Compact Peer Lists | Yes | Yes | 3 | Via UDP, HTTP, and PEX compact format |
| [27](http://bittorrent.org/beps/bep_0027.html) | Private Torrents | Yes | Yes | 2 | Private flag in torrent creation and parsing |
| [44](http://bittorrent.org/beps/bep_0044.html) | Storing Arbitrary Data in DHT | Yes | Yes* | 5 | Desktop: full DHT put/get. Browser: via WebSocket tracker relay (AgentChannel). Ed25519 signed. |
| [46](http://bittorrent.org/beps/bep_0046.html) | Updating Torrents via DHT Mutable Items | Yes | Yes* | 13 | `btpk` magnet URI, auto-update subscription, official test vector validated. Browser: via tracker relay. |
| [48](http://bittorrent.org/beps/bep_0048.html) | Tracker Scrape | Yes | Yes | — | HTTP scrape for seeder/leecher counts without announcing |
| [53](http://bittorrent.org/beps/bep_0053.html) | Magnet URI — Select Specific Files | Yes | Yes | 1 | `so=` parameter parsed into `SelectedFileIndices` |

\* BEP 44/46 browser support uses the WebSocket tracker relay path via `AgentChannel`, not raw DHT UDP. The signing, encoding, sequence handling, and verification logic is identical on both platforms. Only the transport differs.

### Platform Constraints (honest assessment)

Three BEPs are desktop-only due to a hard browser platform constraint: **browsers have no UDP socket API**. This is not a library limitation — it's a browser sandbox constraint that affects all WebRTC-based torrent clients.

| Constraint | Affected BEPs | Workaround |
|-----------|--------------|------------|
| No UDP sockets in browser | BEP 5 (DHT), BEP 14 (LSD), BEP 15 (UDP tracker) | DHT → WebSocket tracker relay for BEP 44/46. LSD → not applicable (browser has no "local network"). UDP tracker → use WSS/HTTP trackers instead. |

## Cryptographic Signing (BEP 44/46)

| Algorithm | Desktop | Browser | Library | Notes |
|-----------|---------|---------|---------|-------|
| **Ed25519** | Yes | Yes | `SpawnDev.BlazorJS.Cryptography` 3.1.0+ | BEP 44 REQUIRED algorithm. 32-byte public keys, 64-byte signatures. |

Ed25519 support was added to `SpawnDev.BlazorJS.Cryptography` specifically for BEP 44/46 compliance. The `Ed25519Signer` class works identically on both platforms:
- **Browser:** WebCrypto API (native C++ — hardware-accelerated on most platforms)
- **Desktop:** .NET `System.Security.Cryptography` Ed25519 implementation

All BEP 44/46 operations (DHT mutable items, AgentChannel pub/sub, `btpk` magnet resolution) use Ed25519 exclusively. ECDSA-P256 was used in earlier versions but has been replaced — Ed25519 is the only signing algorithm for all new SpawnDev code.

## Piece Verification

| Algorithm | Create | Verify | Auto-detect |
|-----------|--------|--------|-------------|
| SHA-1 (20 bytes) | Yes | Yes | For legacy compatibility |
| SHA-256 (32 bytes) | Yes | Yes | Default for new torrents. Auto-detected from piece hash size. |

Verification uses `IPortableCrypto` when available:
- **Browser:** SubtleCrypto (native C++ — orders of magnitude faster than WASM SHA)
- **Desktop:** `System.Security.Cryptography` (already fast)

## Tracker Support

| Type | Desktop | Browser | Protocol |
|------|---------|---------|----------|
| WebSocket (wss://) | Yes | Yes | JSON signaling with WebRTC offer/answer relay |
| HTTP/HTTPS | Yes | Yes | BEP 3 announce + BEP 48 scrape, compact/non-compact peer lists |
| UDP | Yes | No | BEP 15 binary protocol (connect/announce). No UDP in browser. |

## WebRTC Transport

| Platform | Library | Notes |
|----------|---------|-------|
| Browser | SpawnDev.BlazorJS RTCPeerConnection | Full signaling via tracker relay |
| Desktop | SIPSorcery 10.0.3 RTCPeerConnection | Same signaling protocol, cross-platform P2P |

## Not Implemented (high effort / future)

| BEP | Title | Effort | Notes |
|-----|-------|--------|-------|
| [29](http://bittorrent.org/beps/bep_0029.html) | uTorrent Transport Protocol (uTP) | High | Full congestion-controlled UDP transport (LEDBAT). Desktop only. Requires implementing a complete sliding-window protocol stack with loss recovery. |
| [52](http://bittorrent.org/beps/bep_0052.html) | BitTorrent Protocol v2 | Large | Per-file SHA-256 Merkle trees, new info dict format, hybrid v1/v2 torrents. Requires new TorrentParser branch, Merkle tree construction, updated piece verification. |

## Test Coverage

109 shared test methods (via `SpawnDev.UnitTesting`) covering all 17 implemented BEPs, running on both desktop (DemoConsole) and browser (Demo via PlaywrightMultiTest). Plus 66 NUnit desktop-only tests for wire protocol, piece management, seeding, torrent creation, file streaming, rate limiting, and lifecycle management.

All tests use real data, real hashing, real protocol bytes. No mocks. Verified against live WebTorrent swarms (Sintel) and official BEP 46 test vectors.
