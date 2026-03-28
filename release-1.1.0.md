# SpawnDev.WebTorrent v1.1.0 Release Notes

**Release Date:** 2026-03-28
**Previous Release:** v1.0.0 (2026-03-27)

## Highlights

- **Real P2P** — WebRTC peer connections actually work (browser + desktop)
- **13 BEPs implemented** — up from 3 partial in v1.0.0
- **161 unit tests** — up from 49 in v1.0.0
- **Web seed downloading works** — pieces actually download and verify
- **Full torrent client UI** — professional-quality Blazor + WPF demos
- **Zero TODO stubs** — every feature is implemented

## New Features

### P2P Pipeline (was completely non-functional in 1.0.0)
- `TorrentSwarm.AddPeer()` — full connect → handshake → bitfield → piece exchange
- `TorrentSwarm.AddConnectedPeerAsync()` — accept incoming peers
- `WebTorrentClient.HandleIncomingConnection()` — route to correct swarm
- Seeding — respond to Request messages with Piece data
- Choke/unchoke rotation (10s rechoke, 30s optimistic unchoke)
- Endgame mode (request from all peers when <=5 pieces remain)
- Connection keep-alive (60s timer)
- Connection timeout (25s)

### WebRTC (was placeholder in 1.0.0)
- **Browser:** Real `RTCPeerConnection` via SpawnDev.BlazorJS
- **Desktop:** Real `RTCPeerConnection` via SIPSorcery 10.0.3
- `IWebRtcTransport` interface — both platforms share the same contract
- `PeerCoordinator` — full signaling relay through trackers
- Offer/answer exchange with ICE gathering

### BEPs Implemented
| BEP | Feature | New in 1.1.0 |
|-----|---------|:---:|
| 3 | BitTorrent Protocol | Enhanced |
| 5 | DHT (Kademlia) | New |
| 6 | Fast Extension (HaveAll/None/Suggest/Reject/AllowedFast) | New |
| 9 | ut_metadata (request/data/reject, SHA-1 verify) | New |
| 10 | Extension Protocol (handshake parsing) | Enhanced |
| 11 | ut_pex (compact peer list parsing) | New |
| 15 | UDP Tracker (connect/announce) | New |
| 17/19 | Web Seeds (multi-file piece assembly) | Fixed |
| 20 | Peer ID (`-SD0110-`) | Updated |
| 23 | Compact Peer Lists | New |
| 27 | Private Torrents (DHT/PEX peer rejection) | New |
| 53 | Magnet File Selection (`so=` parameter) | New |

### Tracker Support (was WebSocket only)
- **WebSocket** — JSON signaling with WebRTC relay
- **HTTP/HTTPS** — URL-encoded announce, compact/non-compact peer lists
- **UDP** — BEP 15 binary protocol (desktop only)
- **DHT** — Kademlia routing, KRPC protocol (desktop only)

### Storage (was memory only)
- **OPFS** — Browser persistent storage via SpawnDev.AsyncFileSystem
- **FileChunkStore** — Desktop file-based storage
- **MemoryChunkStore** — Fallback

### Client API (many were missing/stub in 1.0.0)
- `client.SeedAsync()` — Create and seed from bytes
- `client.Get()` — Find torrent by info hash
- `client.AddFromUrlAsync()` — Add from .torrent URL
- `client.CreateServer()` — HTTP server with range requests
- `client.Progress`, `client.Ratio` — Aggregate stats
- `client.UploadLimiter`, `client.DownloadLimiter` — Token bucket rate limiting

### Torrent API
- `torrent.Select/Deselect/Critical` — Piece range prioritization
- `torrent.RemovePeerAsync()` — Remove specific peer
- `torrent.RescanFilesAsync()` — Re-verify pieces
- `torrent.MagnetURI` — Generate magnet URI
- `torrent.TorrentFileBytes` — Export .torrent
- `torrent.TimeRemaining` — Download ETA
- `torrent.Ratio` — Seed ratio
- `torrent.IsPrivate` — BEP 27
- `torrent.UpdateSpeed()` — Real-time bytes/sec

### File API
- `file.Select/Deselect` — Per-file download control
- `file.Done`, `file.Downloaded` — Per-file state
- `file.Type` — MIME detection (30+ types)
- `file.Includes(piece)` — Piece membership
- `file.StreamAsync()` — IAsyncEnumerable chunk streaming
- `file.GetArrayBufferAsync()` / `file.GetBlobBytesAsync()`

### Demo Apps

**Blazor WASM:**
- Professional torrent client UI (columns, detail tabs, piece map)
- Media viewer (video/audio/image) with blob URL playback + seeking
- Settings modal (rate limits, tracker list, client info)
- OPFS persistent storage
- WebRTC P2P via PeerCoordinator

**WPF Desktop:**
- Matching professional UI
- Media player (MediaElement + TorrentHttpServer for seeking)
- Drag-drop .torrent files
- Settings dialog
- SIPSorcery WebRTC P2P

## Bug Fixes
- **Web seed download was completely broken** — wrong file for multi-file torrents, `ReceiveBlockAsync` vs `ReceiveCompletePieceAsync`, URL encoding of `/` in paths
- **Magnet URI `+` not decoded as space** — `Uri.UnescapeDataString` doesn't handle `+`
- **Only first tracker connected** — `break` after first success prevented finding real seeders
- **Extension handshake not parsed** — bencode payload was ignored
- **Peer ID was 1.0.0** — bumped to `-SD0110-` for 1.1.0

## Dependencies
- SpawnDev.BlazorJS 3.5.0 (browser WebRTC + OPFS)
- SpawnDev.AsyncFileSystem 1.0.0 (persistent storage)
- SIPSorcery 10.0.3 (desktop WebRTC)

## Stats
- 73 source files
- 15,444 lines of C#
- 161 Playwright tests (158 pass, 3 skip)
- 71 git commits
- 0 TODO/FIXME stubs remaining
