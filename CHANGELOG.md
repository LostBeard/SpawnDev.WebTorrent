# Changelog

## 3.1.3-rc.26 (2026-04-24)

### BEP 44/46 server-side completeness

- `DhtDiscovery.HandleQuery` now dispatches BEP 44 `get` and `put` queries via new `OnGetQuery` / `OnPutQuery` events. `DhtMutableItems` wires those events to a local `_storedItems` cache: verifies Ed25519 signatures on inbound puts (rejects forgeries), serves stored values on subsequent gets. Previously SpawnDev.WebTorrent could only be a BEP 44 client, never a server, so mutable items never propagated in small swarms.
- `DhtMutableItems.PublishAsync` now self-stores so a subscriber querying the publisher directly is served the value (required when the publisher is one of the K closest nodes to the target).
- `DhtMutableItems.SubscribeAsync` bypasses the cache short-circuit - keeps polling for newer sequences instead of returning the first cached value forever.
- BEP 5 learn-from-query: `DhtDiscovery.HandleQuery` now adds the querier's NodeId to the routing table (previously only query responses populated the table). Fixes small-swarm / bootstrap scenarios.
- `Ed25519Signer.VerifyAsync` now accepts raw 32-byte BEP 44 wire-format pubkeys and wraps them in SPKI internally. Previously verify silently failed on every incoming put because `ImportEd25519Key` expects SPKI, not raw bytes. **Real production-path bug, not a test-only issue** — any incoming signed mutable item was rejected.
- New public helpers: `DhtDiscovery.FindNodeAsync(endpoint)` + `PingAsync(endpoint)` for manual-bootstrap / LAN-discovery / unit-test scenarios. New public `BuildGetResponse(txId, nodeId, value, seq, signature, publicKey)` for custom get-query handlers.
- 2 new real-P2P loopback tests: `Bep46_Loopback_PublishSubscribe_DeliversValue` (~5s) + `Bep46_Loopback_Republish_BumpsSequence` (~14s). Two in-process `DhtDiscovery` instances on loopback UDP exercise the full BEP 44/46 round-trip; the first test is the first end-to-end proof the network path works (existing tests stubbed delivery with `NotifyMutableUpdate`).

SpawnDev.WebTorrent is now among the very few BitTorrent clients with a genuinely working BEP 46 implementation - libtorrent / qBittorrent / Transmission / Deluge / WebTorrent-JS all skip BEP 46 first-class (only RangerMauve's `mutable-webtorrent` JS library ships it).

## 3.1.3-rc.23 - rc.25 (2026-04-24)

### Pure-v2 feature parity across UI + streaming + HTTP browser

- **rc.23**: Pure-v2 torrents now work end-to-end through the service-worker media streaming path and the desktop `TorrentHttpServer` file browser. `ServiceWorkerStreamHandler.GetStreamUrl` was emitting `/webtorrent/{InfoHashHex}/{fileIdx}` which collapses to `/webtorrent//{fileIdx}` for pure-v2 (empty v1 InfoHash). Fixed to use `WireInfoHashHex` (first 20 bytes of v2 hash for pure-v2, v1 hash otherwise). `WebTorrentClient.HandleStreamRequest`'s lookup switched to match `WireInfoHashHex` case-insensitively.
- **rc.24**: Dep-bump `SpawnDev.RTC` rc.3 → rc.6 (transitively: PerfectNegotiator W3C glare-free renegotiation helper; BrowserRTCSctpTransport live JSRef reads; race fix). No WebTorrent source changes.
- **rc.25**: `Torrent.DisplayNameShort` narrow-cell helper (Name → first 12 chars of WireInfoHashHex → "unknown"). Blazor demo switches from an inline 3-level ternary to `@torrent.DisplayNameShort`.

## 3.1.3-rc.20 - rc.22 (2026-04-24)

### Pure-v2 in-memory dedup, OPFS persistence, and demo UI polish

- **rc.20**: Duplicate check in `WebTorrentClient.Add(magnet)` / `Add(bytes)` / `SeedFromMetadataAsync` now uses `WireInfoHashHex` instead of raw `InfoHash`. Two calls with the same pure-v2 magnet or bytes previously created two separate Torrent entries (v1 InfoHash is empty for pure-v2 so the dedup check matched everything to everything). Plus `RemoveAsync(string)` and `RemoveWithDataAsync(string)` now delegate to `Get(string)` so callers can remove by v1, full v2, or wire-truncated hash.
- **rc.21**: Pure-v2 OPFS persistence. `Torrent.PersistMetadataAsync` / `PersistStateAsync` / the chunk-store path + `WebTorrentClient.RestoreFromStorageAsync` / `RemoveAsync(Torrent)` / `RemoveWithDataAsync(Torrent)` all key on `WireInfoHashHex` now. Pure-v2 torrents survive page reloads with pieces under `webtorrent/<v2-prefix>/` and metadata at `webtorrent/_state/<v2-prefix>.torrent`. v1-only / hybrid paths unchanged - byte-compatible with existing OPFS data. New `TorrentMetadata.WireInfoHashHex` computed property.
- **rc.22**: `Torrent.DisplayName` helper (Name → WireInfoHashHex → "unknown") so UI code can consume directly without `??` chains that silently miss pure-v2 torrents (`??` only triggers on null, not empty). Blazor + WPF demos updated. Blazor details pane now renders both `Info Hash` (v1) and `V2 Info Hash` (BEP 52) rows conditional on which hashes the torrent has.

## 3.1.3-rc.13 - rc.19 (2026-04-23 / 2026-04-24)

### Pure-v2 tracker + wire + ut_metadata + phantom-wire handshake fix

- **rc.13**: Pure-v2 tracker + wire handshake support (`Torrent.WireInfoHashHex` returns first 20 bytes of v2 SHA-256 for pure-v2 torrents, libtorrent / qBittorrent / rqbit convention). Removes the `NotSupportedException` on `magnet:?xt=urn:btmh:` magnets. `WebTorrentClient.Get(hash)` matches v1 / full v2 / wire-truncated forms. `TorrentCreator.CreateFromMultipleStreamsAsync` streaming multi-file creator (bounded memory for multi-GB HF model shards).
- **rc.14**: ut_metadata BEP 9 extension v2 variant - advertises `metadata_version: 2` in the BEP 10 extended handshake, SHA-256 verifies against the full v2 hash on receipt. Hybrid + v1-only stay on the v1 path unchanged.
- **rc.15**: Diagnostic build for Geordi's rc.12 two-popup mystery. `RtcPeer.OnDataChannel` responder path logs the ChannelName after assignment; `Torrent.OnHandshake` duplicate branch dumps full Wires state (all PeerIds + matching `_peers` + ChannelNames) before the tiebreaker decision.
- **rc.16**: `TorrentCreator.CreateFromMultipleStreamsAsync` now supports `Hybrid=true`. Single-pass streaming feeds each read to both the v2 IncrementalMerkleHasher AND the v1 SHA-1 piece buffer. End-of-file emits a zero-padded v1 piece for non-last files (matching pad-file-filled virtual stream) and a genuine partial piece for the last. Byte-identical to the in-memory BuildHybridMultiFile path.
- **rc.17**: Multi-tracker failover resilience. `Discovery.AnnounceAsync` isolates per-tracker failures - one unreachable WSS host / HTTP 5xx / UDP timeout no longer cancels the aggregate `Task.WhenAll`. Errors surface via `OnWarning`. Closes Gap 6 of Geordi's P2P audit on the WebTorrent side.
- **rc.18**: `WebTorrentClient.Add` wires the ut_metadata factory into v2 mode automatically when the magnet is pure-v2. New `TorrentParser.ParseInfoDictV2(bytes, expectedV2InfoHash)` helper.
- **rc.19**: Phantom-wire filter on the duplicate-peer tiebreaker. Geordi's rc.15 DUP-DIAG output identified orphan Wires entries from destroy-race as the two-popup peerCount=0 root cause. rc.19 filter requires `!w.Destroyed` AND a live backing peer in `_peers`. Closes the multi-iteration (rc.10 → rc.12 → rc.15 → rc.19) paired-debug effort with Geordi; verified GREEN by Geordi in 9s end-to-end via `WasmP2PBrowserTests.ComputeSwarm_Benchmark_RoundTrips_BetweenTwoPopups`.

## 3.1.3-rc.3 - rc.12 (2026-04-23)

- **rc.12**: Duplicate-peer tiebreaker on WebRTC channel Label (cross-side-stable). Replaces the rc.10-11 newcomer/existing axis which was per-side timing-dependent.
- **rc.11**: `RtcPeer` deferred `EmitConnect` when the data channel is already open at subscribe time (responder race).
- **rc.10**: First iteration of the duplicate-peer handshake tiebreaker (superseded by rc.12's correct axis).
- **rc.4**: `RtcPeer.PeerConnection` public getter - unblocked Geordi's MaxBurst consumer path for 10 MB WebRTC dispatch.
- **rc.3**: Dep-bump `SpawnDev.RTC` 1.1.3-rc.1 → 1.1.3-rc.2 (SCTP MaxBurst tunables).

## 3.1.3-rc.2 (2026-04-23)

### Pure-v2 multi-file download support

- **TorrentParser** now walks the whole v2 file tree and flattens each file's piece-layer hashes into `PieceHashes` in file-tree order. Previously only `FileRoots[0]`'s piece layer was emitted, leaving `_hashes[globalIndex]` wrong (or out of range) for any piece belonging to a file past the first. A pure-v2 multi-file torrent created by libtorrent or any external v2-first creator can now verify past file 0.
- **Per-file offsets** are now in the PADDED virtual stream — each file starts on a piece boundary with implicit zero-padding from the previous file's tail, per BEP 52 §"File tree". `TorrentFileInfo.Offset` reflects the virtual layout so the global-piece-index addressing used by BEP 3 wire request/piece messages is well-defined.
- **Per-piece length array** (`Torrent._pieceLengths[]`) replaces the old "every piece is `PieceLength` except the last" assumption. For pure-v2 multi-file, each file's last piece is `file.Length % PieceLength` bytes long; pieces straddling a file-end boundary are sized correctly so `Piece.Flush()` returns the right length and `VerifyPieceHash` hands an appropriately-short buffer to `MerkleHasher.ComputePieceLayer` (which handles leaf-level zero-padding internally).
- **TorrentCreator** (`BuildV2MultiFile`) now sorts input files by path into BEP 52 file-tree walk order before building per-file structures. Flat `PieceHashes` sequence matches what a parse round-trip produces.
- New NUnit test `VerifyPieceHashTests.V2_PureMultiFile_AllPiecesVerify_PastFile0`: creates a 3-file pure-v2 torrent (with partial last pieces on files 2 + 3), parses round-trip, walks every file's pieces and asserts `VerifyPieceHash(globalIdx, piece)` succeeds for all of them. Passes in 22ms. Mirror browser test `Bep52_PureV2MultiFile_AllPiecesVerify_PastFile0` added to the cross-platform suite.
- Existing test `TorrentCreatorV2MultiFileTests.MultiFile_V2_FlatFiles_ProducesFileTreeWithMultipleLeaves` updated: the second file's offset now asserts against the PADDED value (16384 instead of 500) since BEP 52 requires that layout for multi-file v2.
- Full NUnit regression: **256/0/0 in 5s** (from 255, +1 new test, 0 regressions).

### Wire._message + Rechoke timer hardening (concurrent work by Geordi's session)

- `Wire._message` now issues one `_push` per logical Wire message instead of two (header + data). Each BEP 10 extension message previously became two separate SCTP user messages on the WebRTC data channel; SCTP paced each independently, which stacked on top of SCTP `MAX_BURST` / `BURST_PERIOD` rate limiting and bottlenecked SpawnDev.ILGPU.P2P multi-MB tensor transfers at ~100 KB/s end-to-end despite the underlying pipe sustaining 5.4 MB/s on single-send payloads (see 3.1.3-rc.1 SctpDataSender fix).
- Hardens `Torrent.DisposeAsync` against late-firing Rechoke timer callbacks by using `Timer.DisposeAsync` to drain in-flight work, plus a defensive null filter + try/catch inside the callback.

## 3.1.3-rc.1 (2026-04-23)

### Dep-bump: SCTP sender throughput fix (via SpawnDev.RTC 1.1.3-rc.1)

- Dep-bump to `SpawnDev.RTC 1.1.3-rc.1` which transitively picks up `SpawnDev.SIPSorcery 10.0.5-rc.1` and its `SctpDataSender` lost-wakeup fix. **60x on the zero-RTT synthetic benchmark** (89.8 KB/s → 5.4 MB/s). **Real-world end-to-end throughput stays bounded by `MAX_BURST × MTU / RTT`** (~186 KB/s on loopback) until `MAX_BURST` is tunable — Geordi re-measured ~0.15–0.19 MB/s regardless of buffer size through a real DesktopRTCPeerConnection. The fix is correct; the headline 60x number only manifests when SACK RTT is effectively zero. See `SpawnDev.RTC/Docs/sctp-tuning.md` for the full analysis.
- No SpawnDev.WebTorrent source changes. Pure dep refresh.
- Full NUnit regression: **255/0/0 in 5s** (same as 3.1.2 stable since the WebTorrent library itself didn't change).
- Intended for SpawnDev.ILGPU.P2P to consume. Unblocks multi-MB tensor transfers over WebRTC data channels.

## 3.1.2 (2026-04-22 stable)

### BEP 52 v2 complete: peer-wire extension + Torrent integration + browser coverage

- **BEP 52 (BitTorrent v2) is feature-complete.** Merkle-tree piece verification, 16 KiB leaves with per-level pad-hash propagation, per-file Merkle roots, piece layers, SHA-256 info hash, `urn:btmh:` magnet URIs, hybrid v1+v2 info dicts, and the full peer-wire extension (messages 21 `hash_request` / 22 `hashes` / 23 `hash_reject`). See `Docs/bep52.md` for the walkthrough.
- Peer-wire state machine: `V2HashRequestCoordinator` allocated per v2 torrent, correlates outbound `hash_request` to inbound `hashes` on any connected wire (per-torrent, not per-wire). Handles timeout, cancellation, duplicate-key rejection, cryptographic verification, hash_reject as `HashRejectedException`.
- Seed path: `Torrent.OnV2HashRequest` uses `MerkleProofBuilder` to emit `hashes` payload from our `PieceLayers` dict, with self-check that the emitted proof re-climbs to the advertised pieces_root before transmission.
- Client path: `Torrent.RequestV2HashesAsync(req, ct, wire?)` routes through the coordinator + a picked peer Wire's `SendHashRequest`.
- Critical correctness: `Torrent.VerifyPieceHash` now branches on `MetaVersion`. v2 torrents verify against the Merkle piece-layer root (not a flat SHA-256 of the piece bytes) — this was a latent bug where large-piece-size v2 torrents would always mismatch. Caught and fixed before any user impact.
- Streaming hybrid single-file creation (`CreateHybridSingleFileFromStreamAsync`) for multi-GiB torrents in bounded memory. Multi-file hybrid inserts spec-correct pad files (`attr="p"`, `path=[".pad","N"]`).
- HuggingFaceProxy cutover to `Hybrid = true` by default. Every HF model torrent now carries both v1 SHA-1 and v2 SHA-256 infohashes.
- Cross-platform browser coverage via `WebTorrentTestBase.Bep52V2Tests.cs` — 16 tests × 2 projects through PlaywrightMultiTest. Peer-wire mirror added in the final step for 10 more browser-covered tests.
- **Test totals:** NUnit desktop 255/0/0 in 5s (from 68 pre-Phase-2 baseline + 187 new BEP 52 tests). Zero regressions on the v1 path anywhere.
- Full BEP coverage matrix: **19 BEPs** (added BEP 52). See `Docs/bep-support.md`.

### Contributing fork fixes (shipped in this version's transitive deps)

- SIPSorcery fork: `SortMediaCapability` priority-track inverted ternary fixed (upstream PR [sipsorcery-org/sipsorcery#1558](https://github.com/sipsorcery-org/sipsorcery/pull/1558)). Two peers with identical multi-codec audio lists now agree on a single negotiated format, instead of one side seeing PCMU and the other seeing Opus.

## 3.1.1 (2026-04-22)

### Packaging fix for CI / GitHub Pages consumers

- Dep-bump to `SpawnDev.RTC 1.1.1` (was `1.1.0`, which declared a dep on the un-published `SIPSorcery 10.0.4-pre` fork ID and failed to restore in external builds).
- `SpawnDev.RTC` is now a `PackageReference` (previously `ProjectReference`) so standalone WebTorrent checkouts build without the sibling RTC repo.
- `SpawnDev.WebTorrent.Server` reference to `SpawnDev.RTC.Server` similarly swapped from `ProjectReference` to `PackageReference`.
- No code changes from 3.1.0.

## 3.1.0 (2026-04-22 stable)

### Renamed fork consumption + tracker-signaling flow

- First stable cut after the WebTorrent 3.x restructure where the tracker moved to `SpawnDev.RTC.Server`. WebTorrent.Server is now web-seed-only.
- Captain manually verified bidirectional JS WebTorrent interop through hub.spawndev.com: JS seeder → JS downloader, AND JS seeder → SpawnDev.WebTorrent C# downloader. Both complete.
- Full PlaywrightMultiTest regression: 408/0/13 at this cut.

### Earlier 3.0.x development

- See git history for pre-stable 3.0.0-rc.* and 3.0.1-rc.* milestones (BEP 52 Phase 1 foundation, streaming piece flush, service worker range streaming, OPFS persistence, etc.).
