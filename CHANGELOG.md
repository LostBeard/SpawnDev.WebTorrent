# Changelog

## 3.1.3-rc.1 (2026-04-23)

### Dep-bump: SCTP sender throughput fix (via SpawnDev.RTC 1.1.3-rc.1)

- Dep-bump to `SpawnDev.RTC 1.1.3-rc.1` which transitively picks up `SpawnDev.SIPSorcery 10.0.5-rc.1` and its `SctpDataSender` lost-wakeup fix. 60x loopback throughput win measured on the fork's new regression test (89.8 KB/s → 5.4 MB/s). See `SpawnDev.RTC/Docs/sctp-tuning.md` for the full analysis.
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
