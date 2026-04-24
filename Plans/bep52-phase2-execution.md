# BEP 52 Phase 2 Execution Plan

**Goal:** Ship full BEP 52 v2 compliance in SpawnDev.WebTorrent: Merkle-tree piece verification, per-file piece alignment, hybrid v1+v2 info dicts, and bidirectional interop with `libtorrent` / qBittorrent v2 torrents.

**Current state (2026-04-23, mid-session update):** Phase 2a + 2b + 2c (steps 1 + 2 entire + 2.3b-integration) all shipped. BEP 52 Phase 1 was in commit `de92f8d` (WebTorrent 3.1.0-rc.3). Phase 2 commits below land proper Merkle-tree v2 + hybrid support + full peer-wire BEP 52 extension.

## Shipped this session (2026-04-23)

| Commit | Sub-phase | Description |
|---|---|---|
| `43ab1db` | 2 foundation | `MerkleHasher` (pad-hash propagation formula, 16 KiB leaves) + this plan + 29 tests (incl. piece-size invariance). |
| `a621878` | 2a step 1 | v2 single-file creator + parser + `TorrentMetadata` schema bump + `ByteArrayEqualityComparer` + `BencodeDecoder.DecodeDictionaryRawKeys` + 12 tests. |
| `1b15a7f` | 2a step 2 | `IncrementalMerkleHasher` (bounded-memory streaming Merkle tree) + v2 streaming path in `CreateFromStreamAsync` + 35 tests. |
| `a4b5e57` | 2a step 3 | v2 multi-file (in-memory, no alignment) + nested file tree + sorted piece layers + 11 tests. |
| `d7082c2` | 2b step 1 | Hybrid v1+v2 single-file. Combined info dict, two infohashes. `TorrentCreatorOptions.Hybrid` option. 7 tests. |
| `178ee72` | 2b step 2 | Hybrid v1+v2 multi-file with pad files (`attr="p"`, `path=[".pad","N"]`) between misaligned real files. 6 tests. |
| `ddf2a32` | 2c step 1 | v2 magnet URI parsing (`xt=urn:btmh:1220<digest>`) + hybrid magnet. `Torrent.V2InfoHash` property + `ComputedMagnetUri` emits both. 12 tests. |
| `42269ac` | 2b follow-up | Streaming hybrid single-file: `CreateHybridSingleFileFromStreamAsync` for multi-GiB in bounded memory. 3 new tests. |
| `308bb65` | HF cutover | HuggingFaceProxy flipped to `Hybrid = true` by default. Every HF model torrent now carries both infohashes. |
| `8c9835b` | PW coverage | 8 v2 tests in `WebTorrentTestBase.Bep52V2Tests.cs` × 2 projects = 16 cross-platform runs via SpawnDev.UnitTesting. |
| `77469ab` | 2c step 2 fdn | `Bep52WireMessages` record structs + Encode/Decode for msg ids 21/22/23 (big-endian u32 wire format). 10 tests. |
| `d019552` | docs | `Research/` docs added: WebTorrent / tracker / DHT / SipSorcery / DTLS reference (10 files, 7,216 lines). |
| `b92051a` | 2c step 2.1 | `MerkleProofVerifier` - pure verify function + `Hashes`-message overload. 13 tests incl. 8-leaf middle range. |
| `f665103` | 2c step 2.2 | `Wire.cs` dispatch: `OnHashRequest`/`OnHashes`/`OnHashReject` events + `SendHashRequest`/`SendHashes`/`SendHashReject`. 8 tests including full peer-to-peer loopback. |
| `5389c5c` | 2c step 2.3a | `Torrent.VerifyPieceHash` branches on MetaVersion - **fixes latent v2 bug** where large-piece torrents always mismatched against their stored Merkle roots. 9 tests incl. real creator-to-parser-to-verify pipeline. |
| `cf3779d` | 2c step 2.3b | `V2HashRequestCoordinator` state machine: RequestAsync correlation, timeout, verification, cancellation, dup-key rejection. 10 tests. |
| `cd521e8` | 2c step 2.3b-int | Torrent ↔ coordinator glue: per-torrent `V2HashCoord` + event forwarding in `OnWireWithMetadata`, `OnV2HashRequest` seed path via `MerkleProofBuilder`, public `RequestV2HashesAsync` API. 23 tests. |

**Test totals:** 255/0/0 in full WebTorrent NUnit suite (68 pre-Phase-2 baseline + 187 new BEP 52 tests; zero regressions on v1 path).

This plan scopes the remaining Phase 2 work into sub-phases so each lands as a working, tested, committed release candidate rather than one multi-week megacommit.

---

## Scope breakdown

### Phase 2a: Merkle hasher + v2-only torrent creation/parsing

**Deliverables**
- `MerkleHasher` static/helper class implementing BEP 52's piece-root and file-root Merkle algorithms (SHA-256, 16 KiB leaves, zero-pad to power of 2).
- Known-answer tests against vectors from BEP 52 and/or a libtorrent-generated reference torrent.
- `TorrentCreator` v2-only path: writes a .torrent with `meta version = 2`, `file tree`, `piece layers`. Uses real Merkle roots per file.
- `TorrentParser` v2-only path: parses v2 info dict (recursive `file tree`), reads `piece layers`, computes v2 info hash (SHA-256 of info dict).
- `TorrentMetadata` gains `MetaVersion`, `FileRoots` (per-file Merkle root), `PieceLayers` (flat root-to-hash-array map), `V2InfoHash` fields. Existing v1 fields untouched.
- Verification path (`Torrent.Download.cs`) in v2 mode checks each piece against the Merkle-derived piece-layer hash.

**Phase 2a status: SHIPPED** (commits `43ab1db` / `a621878` / `1b15a7f` / `a4b5e57`).

**Explicit non-goals for 2a**
- No hybrid v1+v2 torrents yet (pure v2 only). (Shipped in 2b.)
- No v2 magnet URI (`urn:btmh:`) parsing yet. (Shipped in 2c step 1.)
- No `ut_hash_request` extension yet (no partial-tree fetch over the wire).

### Phase 2b: Per-file alignment + hybrid v1+v2 info dict

**Deliverables**
- [x] `TorrentCreator` hybrid path: single pass over input files produces BOTH v1 flat SHA-1 hashes AND v2 Merkle tree. Emits a single info dict with both `pieces` (v1) and `meta version`/`file tree`/`piece layers` (v2). Two valid infohashes. SHIPPED in `d7082c2` (single-file) + `178ee72` (multi-file).
- [x] Per-file piece alignment: each file starts at a piece boundary; pad files (`attr="p"`, path=`[".pad","N"]`) inserted between real files in the v1 files list so the v1 and v2 interpretations see identical piece-aligned content. SHIPPED in `178ee72`.
- [x] `TorrentParser` hybrid detection: when both sets of fields are present, both infohashes are populated. SHIPPED in `a621878` (detection logic landed with parser v2 support; exercised by hybrid tests in `d7082c2` / `178ee72`).
- [ ] `HuggingFaceProxy` generator switches to hybrid output. Not yet done - the library primitive (`TorrentCreatorOptions.Hybrid = true`) is ready; the proxy needs a follow-up PR to flip its default.

**Phase 2b status: SHIPPED** (HF proxy cutover landed in `308bb65` - every HF torrent now hybrid by default).

**Explicit non-goals for 2b**
- No wire-protocol changes yet (the `bittorrent v2` extension negotiation isn't wired). A v2 peer only actually verifies with Merkle if it found the v2 infohash via a v2 magnet or a v2-aware .torrent file. See 2c.

### Phase 2c: Ecosystem + wire interop

**Deliverables**
- [x] `urn:btmh:` v2 magnet parsing (multihash-prefixed, SHA-256 variant). SHIPPED in commit `ddf2a32`.
- [x] v2 peer protocol messages (BEP 52 §`Protocol extension`): types 21 (hash_request), 22 (hashes), 23 (hash_reject). Core peer wire, not BEP 10. SHIPPED across `77469ab` (codec), `f665103` (Wire.cs dispatch + events), `cf3779d` (coordinator state machine), `cd521e8` (Torrent ↔ coordinator glue + seed path via `MerkleProofBuilder`).
- [x] Merkle proof verification during piece download (`Torrent.Download.cs`): stored-hash path in `5389c5c` (`VerifyPieceHash` is MetaVersion-aware - v2 uses `MerkleHasher.ComputePieceLayer` instead of flat SHA-256). Missing-piece-layer fetch via `Torrent.RequestV2HashesAsync` + `V2HashRequestCoordinator` integrated in `cd521e8` - foundation for v2-only magnet bootstrap (peer wire can now ask for and serve piece layers mid-download).
- [ ] Interop tests: generate a hybrid torrent with SpawnDev.WebTorrent, load it in libtorrent / qBittorrent, seed from one side to the other, verify both v1 and v2 peers fetch pieces correctly. Same in reverse: parse a libtorrent-generated v2 torrent and verify pieces.
- [ ] Documentation + example in `Docs/`.

**Estimated effort remaining:** 1 day for interop tests against real external clients + a short Docs/bep52.md walkthrough.

---

## Remaining Phase 2 work

1. **Phase 2c step 3: external-client interop tests.** Split-complete:
   - ~~**Parse-level interop against libtorrent-generated v2 torrents.**~~ **SHIPPED 2026-04-23** (`a73b68c`, refined `07d27c3`). 4 reference fixtures pulled from `github.com/arvidn/libtorrent/tree/RC_2_0/test/test_torrents` (v2, v2_multipiece_file, v2_only, v2_hybrid), embedded as assembly resources in `SpawnDev.WebTorrent.Demo.Shared/InteropFixtures/`, exercised by `WebTorrentTestBase.LibtorrentInteropTests.cs` under PlaywrightMultiTest. Each test asserts meta_version, piece_length, name, V2 info hash (SHA-256 of info-dict slice), PieceHashAlgorithm, file count, and re-hashes InfoDictBytes as a defense-in-depth check on slice boundaries. `SpawnDev.WebTorrent.Demo.Shared/InteropFixtures/regenerate_fixtures.cs` is a zero-dep .NET 10 single-file script (`dotnet run` directly) that re-fetches the fixtures from libtorrent's GitHub corpus and regenerates the manifest — byte-identical reproduction verified.
   - **Still pending:** qBittorrent manual drag-drop test + end-to-end cross-seeding with external clients. Runbook in `PLAN-BEP52-External-Interop.md`.

2. ~~**`Docs/bep52.md` walkthrough.**~~ **SHIPPED** (`29a6fe9`). One-page guide covering hybrid creation, magnet URIs, piece verification branching, peer-wire extension, interop with v1-only peers, and full file-reference map.

3. ~~**PlaywrightMultiTest coverage for Phase 2c step 2.**~~ **SHIPPED** (`29a6fe9`). 10 new tests in `WebTorrentTestBase.Bep52V2Tests.cs` cover peer-wire codec round-trips, Merkle proof verifier / builder round-trip, coordinator state machine happy + reject paths, Torrent ↔ coordinator allocation, v2 piece verification via Merkle path, and seed-path payload building.

4. ~~**All BEP 52 tests run under PlaywrightMultiTest (browser + desktop), not just desktop NUnit.**~~ **SHIPPED 2026-04-23** (commits `a50d1d9`, `66487f6`, `77b61d2`). The entire `SpawnDev.WebTorrent.Tests` NUnit project (258 tests across 20 files) retired and migrated into `WebTorrentTestBase.*.cs` partial files. Per-topic naming prefixes (MerkleHasher_, Bep52Wire_, CreatorV2_, V2HashCoord_, etc.) to avoid cross-partial collisions. Duplicate coverage de-duplicated against existing shared partials (PieceTests, ReadFileTests, TorrentCreatorTests had 100% overlap and were removed). 435 tests now enumerate through PlaywrightMultiTest for both runtime matrices; prior state was 258 desktop-only NUnit + 168 shared Playwright.

## Known limitations / followups

- ~~**Pure-v2-only multi-file piece addressing.**~~ **FIXED 2026-04-23** via Option B (implicit pad alignment at parse + create time). `TorrentParser` now walks all files in file-tree order, flattens per-file piece-layer hashes into `PieceHashes`, and pads per-file offsets up to the next piece boundary so each file starts aligned in the virtual stream (per BEP 52's spec). `TorrentCreator.BuildV2MultiFile` mirrors the ordering + padding so creator/parser round-trips are byte-consistent. `SetMetadata` computes per-piece `_pieceLengths[]` from per-file tails so `Pieces[i]` is allocated with the correct short length for any piece straddling a file end. New regression test `VerifyPieceHashTests.V2_PureMultiFile_AllPiecesVerify_PastFile0` exercises 3 files × multi-piece with a partial last piece - passes. `_hashes[globalIndex]` model unchanged; download engine needed no structural refactor. Offset change: `TorrentFileInfo.Offset` for pure-v2 multi-file now reflects the PADDED virtual stream (previously cumulative-raw); existing test `MultiFile_V2_FlatFiles_ProducesFileTreeWithMultipleLeaves` updated to assert the corrected value.

- ~~**Phase 2c step 2 leaf-level (`base_layer = 0`) hash_request serving.**~~ **SHIPPED 2026-04-23** (commit `1416617`). New async `Torrent.TryBuildV2HashesPayloadAsync` reads piece content from the chunk store, re-hashes each 16 KiB leaf, builds the file's full leaf layer, and delegates to `MerkleProofBuilder`. `OnV2HashRequest` gates with a sync `CanPossiblyServeLeafLevel` check so unknown-root / wrong-base-layer / missing-pieces cases still reject synchronously. 3 new tests cover served + missing-pieces + sync-reject paths; Blazor WASM mirror in `Bep52V2Tests.cs`.

---

## Architecture notes

### Why a standalone `MerkleHasher` class

- BEP 52's Merkle construction is self-contained. Isolating it makes it testable without standing up a full torrent creator.
- Tests can use small synthetic inputs (1 KiB, 17 KiB, 33 KiB, 16 MiB, etc.) to exercise every zero-pad edge case.
- Reusable: piece verification, file-root computation, hash-request responses (2c) all call the same primitives.

### BEP 52 Merkle specifics we have to get right

- **Leaf size = 16 KiB** always. Not tied to piece size.
- **Pad last leaf with zero bytes** if the file doesn't end on a 16 KiB boundary. Hash the padded leaf.
- **Pad last level of the tree** to the next power of 2 with a precomputed `pad_hash` layer. The pad hash is SHA-256 of a block of zeros at the bottom, then SHA-256(pad_hash ++ pad_hash) at each subsequent level - NOT just SHA-256(zeros) propagated naively.
- **Piece-layer hashes:** for each piece-sized chunk, the Merkle root of that chunk's 16 KiB leaves. These are what go into the `piece layers` dict.
- **File root:** for a file whose length equals the piece size or smaller, the file root IS its single piece-layer hash. For larger files, the file root is the Merkle root of all piece-layer hashes, again zero-padded to a power of 2 with per-layer pad hashes.
- **SHA-256 throughout.** Use `System.Security.Cryptography.SHA256.HashData` (works on desktop and Blazor WASM, same as Phase 1).

### Test vectors

- Spec doesn't publish canonical test vectors beyond the pad-hash formula.
- Options:
  - Generate a reference v2 torrent with libtorrent's `make_torrent` example or qBittorrent, extract its `piece layers` + file root hashes, and diff our output byte-for-byte.
  - Synthetic self-consistency test: hash a known byte sequence at multiple piece sizes and check that reducing piece size doesn't change the file root (a property that MUST hold by the spec's construction).
- Preferred: both. Self-consistency proves the algorithm is right; libtorrent diff proves our constant-padding matches the reference.

---

## Rule 1 compliance

Each sub-phase is individually shippable. No half-v2 code lands on master. The plan explicitly separates "pure v2 torrent creation" (2a) from "hybrid v1+v2" (2b) because they have different interop consequences and different test surfaces. Cutting a 2a release that works for SpawnDev-internal torrents while 2b is still cooking is acceptable - we own both ends of our server-generated torrent path.

## Rule 2 compliance

No workarounds. If we encounter a spec ambiguity, we check libtorrent's behavior (its codebase is the de-facto v2 reference) and match it. If there's a genuine upstream library bug in SpawnDev.BlazorJS.Cryptography or SipSorcery that affects us here, we fix it there first (same pattern we just used for the SipSorcery codec-priority fix today).
