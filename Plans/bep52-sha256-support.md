# BEP 52 — BitTorrent v2 SHA-256 Support

## Why

SHA-1 has known collision vulnerabilities (SHAttered, 2017). For ML model delivery at scale — hundreds of MB per model, thousands of models, millions of pieces — the collision risk grows. BitTorrent v2 (BEP 52) addresses this with SHA-256 piece hashes.

Credit: Gemini flagged this during a conversation with TJ on 2026-03-27.

## What BEP 52 Changes

1. **SHA-256 piece hashes** instead of SHA-1 (32 bytes vs 20 bytes per piece)
2. **Merkle hash tree** — pieces verified via Merkle root, not flat hash list
3. **Per-file piece alignment** — each file starts at a piece boundary
4. **Hybrid torrents** — can contain both v1 (SHA-1) and v2 (SHA-256) info dicts for backwards compatibility

## Implementation Plan

### Phase 1: SHA-256 Verification (our server-generated torrents) — SHIPPED 2026-04-22 (commit `de92f8d`, SpawnDev.WebTorrent 3.1.0-rc.3)
- [x] `TorrentCreator` — `HashAlgorithm` option on `TorrentCreatorOptions`; defaults to `"SHA-256"`. Set `"SHA-1"` for v1 back-compat.
- [x] `TorrentMetadata` — `PieceHashes` stores hashes at their native byte length; new derived `PieceHashAlgorithm` property returns `"SHA-256"` / `"SHA-1"` / `null`.
- [x] Piece verification (`Torrent.Download.cs` + `Torrent.RescanFilesAsync`) — branches on stored hash length (32 vs 20) before computing, so SHA-256 torrents don't pay the SHA-1 cost.
- [x] `HuggingFaceProxy` — uses the default (SHA-256) for generated model torrents.
- [x] Uses `System.Security.Cryptography.SHA256.HashData` directly (works on both desktop and Blazor WASM). `IPortableCrypto` was the original plan but not needed - the synchronous in-memory hash is fine.
- [x] Tests: `CreateFromBytes_Sha256_RoundTripsThroughParser` (NUnit), `Creator_FromBytes_SHA256_RoundTrip` + `Metadata_PieceHashAlgorithm_DetectsSha1` (Demo.Shared cross-platform). Full suite 408/0/13 regression-clean.

### Phase 2: Full BEP 52 Compliance — SHIPPED 2026-04-23 (SpawnDev.WebTorrent 3.1.2 stable + 3.1.3-rc.2)
- [x] Merkle hash tree for piece verification — `MerkleHasher` (leaf-size invariance property tested), `IncrementalMerkleHasher` (streaming variant for multi-GiB), `MerkleProofVerifier` / `MerkleProofBuilder` pair for peer-wire exchange.
- [x] Per-file piece alignment in torrent creation — hybrid (explicit pad files) + pure-v2 (implicit virtual-stream padding in parser/creator).
- [x] Hybrid v1+v2 info dict for backwards compatibility — `TorrentCreatorOptions.Hybrid = true`; both SHA-1 + SHA-256 infohashes emitted.
- [x] Parse and serve both v1 and v2 torrents from other clients — `TorrentParser` detects meta version, walks file tree, reads piece layers dict (binary-keyed). Peer-wire messages 21/22/23 fully implemented (`Bep52WireMessages` + `V2HashRequestCoordinator` + `Torrent.OnV2HashRequest` seed path).

### Phase 3: Ecosystem — PARTIALLY SHIPPED 2026-04-23
- [x] Magnet URI v2 format (`urn:btmh:` multihash) — `Torrent.ParseMagnet` accepts `urn:btmh:1220<sha256-hex>`; `ComputedMagnetUri` emits hybrid magnets with both `urn:btih:` + `urn:btmh:` when both infohashes are available.
- [x] JS WebTorrent interop verified — Captain manually round-tripped content between his Blazor-WASM JS-WebTorrent wrapper and SpawnDev.WebTorrent.Demo via `hub.spawndev.com:44365/announce`; full download end-to-end (see `reference_webtorrent_js_interop_proof.md`).
- [ ] libtorrent / qBittorrent v2-peer-wire interop — plan + runbook ready in `Plans/PLAN-BEP52-External-Interop.md`; largely manual (needs external tools). Not blocking any production consumer today.

## Who Supports BEP 52
- **libtorrent** (rasterbar) — full v2 support since 2.0
- **qBittorrent** — v2 support since 4.4.0
- **Transmission** — partial support
- **Deluge** — in progress
- We would NOT be alone

## Quick Win
For our own server-generated model torrents, we control both creation and verification. We can switch to SHA-256 immediately for these — no interop concerns. Just change the hash algorithm in `TorrentCreator` and `VerifyPieceAsync`.

## Files to Change
- `Torrent/TorrentCreator.cs` — hash algorithm option
- `Torrent/TorrentMetadata.cs` — SHA-256 hash storage + detection
- `Torrent/PieceManager.cs` — already async, just pass "SHA-256" instead of "SHA-1"
- `Server.HuggingFace/HuggingFaceProxy.cs` — use SHA-256 for generated torrents
- `Torrent/TorrentParser.cs` — parse v2 info dict
