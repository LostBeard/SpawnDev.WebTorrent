# BEP (BitTorrent Enhancement Proposal) Support

Status of BEP implementation in SpawnDev.WebTorrent.

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
| [20](http://bittorrent.org/beps/bep_0020.html) | Peer ID Conventions | Yes | Yes | 2 | Azureus-style: `-WW{ver}-` (WebTorrent-compatible) + random suffix |
| [23](http://bittorrent.org/beps/bep_0023.html) | Tracker Returns Compact Peer Lists | Yes | Yes | 3 | Via UDP, HTTP, and PEX compact format |
| [27](http://bittorrent.org/beps/bep_0027.html) | Private Torrents | Yes | Yes | 2 | Private flag in torrent creation and parsing |
| [44](http://bittorrent.org/beps/bep_0044.html) | Storing Arbitrary Data in DHT | Yes | Yes* | 5 | Desktop: full DHT put/get. Browser: via WebSocket tracker relay (AgentChannel). Ed25519 signed. |
| [46](http://bittorrent.org/beps/bep_0046.html) | Updating Torrents via DHT Mutable Items | Yes | Yes* | 13 | `btpk` magnet URI, auto-update subscription, official test vector validated. Browser: via tracker relay. |
| [48](http://bittorrent.org/beps/bep_0048.html) | Tracker Scrape | Yes | Yes | — | HTTP scrape for seeder/leecher counts without announcing |
| [52](http://bittorrent.org/beps/bep_0052.html) | BitTorrent v2 (Merkle-tree piece verification) | Yes | Yes | ~180 | SHA-256 info hash, 16 KiB Merkle leaves, per-file roots, piece layers, `urn:btmh:` magnet, hybrid v1+v2 (creator + parser), peer-wire extension (messages 21/22/23), `V2HashRequestCoordinator` state machine + seed path. See [`bep52.md`](bep52.md). |
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
| **Ed25519** | Yes | Yes | `SpawnDev.BlazorJS.Cryptography` 3.2.0+ | BEP 44 REQUIRED algorithm. 32-byte public keys, 64-byte signatures. |

Ed25519 support was added to `SpawnDev.BlazorJS.Cryptography` specifically for BEP 44/46 compliance. The `Ed25519Signer` class works identically on both platforms:
- **Browser:** WebCrypto API (native C++ — hardware-accelerated on most platforms)
- **Desktop:** .NET `System.Security.Cryptography` Ed25519 implementation

All BEP 44/46 operations (DHT mutable items, AgentChannel pub/sub, `btpk` magnet resolution) use Ed25519 exclusively. ECDSA-P256 was used in earlier versions but has been replaced — Ed25519 is the only signing algorithm for all new SpawnDev code.

## Piece Verification

| Algorithm | Create | Verify | Auto-detect |
|-----------|--------|--------|-------------|
| SHA-1 (20 bytes) | Yes | Yes | For legacy compatibility |
| SHA-256 (32 bytes) | Yes | Yes | Default for new torrents. Auto-detected from piece hash size. |

Verification routes through `IPieceHashEngine` (default `SystemCryptoPieceHashEngine` — `System.Security.Cryptography` SHA-1 / SHA-256, hardware-accelerated SHA-NI on modern CPUs and natively wired in browser via WebCrypto where available). Slot in a custom engine via `WebTorrentClient.PieceHashEngine` to route piece verification through GPU / batched implementations. See [hash-engine.md](hash-engine.md).

## Tracker Support

| Type | Desktop | Browser | Protocol |
|------|---------|---------|----------|
| WebSocket (wss://) | Yes | Yes | JSON signaling with WebRTC offer/answer relay (provided by `SpawnDev.RTC.Server`) |
| HTTP/HTTPS | Yes | Yes | BEP 3 announce + BEP 48 scrape, compact/non-compact peer lists. Set `WebTorrentClientOptions.AdvertiseTcpListenerToTrackers = true` to be listed at your inbound TCP port. |
| UDP | Yes | No | BEP 15 binary protocol (connect/announce). No UDP in browser. |

## WebRTC Transport

| Platform | Library | Notes |
|----------|---------|-------|
| Browser | [`SpawnDev.RTC`](https://github.com/LostBeard/SpawnDev.RTC) → BlazorJS `RTCPeerConnection` | Native browser WebRTC via SpawnDev.BlazorJS typed wrappers |
| Desktop | [`SpawnDev.RTC`](https://github.com/LostBeard/SpawnDev.RTC) → SipSorcery fork (10.0.5+) | Forked SipSorcery preserves browser-interop DTLS/SRTP + adds SCTP MaxBurst tunables, ResolveHmacKey hook, RelayPortRange |

One `RtcPeer` API on both sides; the underlying transport is selected automatically.

## TCP Peer Wire (desktop only)

| Direction | Class | Notes |
|-----------|-------|-------|
| Outbound | `TcpPeer.ConnectAsync(ip:port)` | Mainline-style direct connect to a known peer. |
| Inbound | `TcpListenerService` (via `WebTorrentClientOptions.TcpListenPort`) | Accepts inbound BitTorrent peer-wire connections. Peeks the 68-byte handshake, routes by info_hash to the matching torrent. See [tcp-listener.md](tcp-listener.md). |

## Not Implemented (high effort / future)

| BEP | Title | Effort | Notes |
|-----|-------|--------|-------|
| [29](http://bittorrent.org/beps/bep_0029.html) | uTorrent Transport Protocol (uTP) | High | Full congestion-controlled UDP transport (LEDBAT). Desktop only. Requires implementing a complete sliding-window protocol stack with loss recovery. Browser has no UDP API. |

## Test Coverage

468+ shared test methods running on BOTH browser AND desktop runtimes via PlaywrightMultiTest (~936 test executions per full sweep), plus libtorrent 2.0 external-interop fixtures, plus three live-network interop tests (qBittorrent forward + reverse via `addPeers`, Node.js `webtorrent@^2` via local SpawnDev.RTC tracker). The historical NUnit-only `SpawnDev.WebTorrent.Tests` project was retired 2026-04-23 — every former NUnit test now runs through `WebTorrentTestBase` partials so coverage exercises both runtimes.

All tests use real data, real hashing, real protocol bytes. No mocks. Verified against live WebTorrent swarms, qBittorrent 5.x + libtorrent 2.0, JS WebTorrent reference, and official BEP 46 test vectors.
