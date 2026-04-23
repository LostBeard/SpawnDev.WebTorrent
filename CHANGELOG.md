# Changelog

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
