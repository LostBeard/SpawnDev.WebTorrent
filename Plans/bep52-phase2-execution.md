# BEP 52 Phase 2 Execution Plan

**Goal:** Ship full BEP 52 v2 compliance in SpawnDev.WebTorrent: Merkle-tree piece verification, per-file piece alignment, hybrid v1+v2 info dicts, and bidirectional interop with `libtorrent` / qBittorrent v2 torrents.

**Current state (2026-04-23, end of session):** Phase 2a + 2b + 2c step 1 all shipped. BEP 52 Phase 1 was in commit `de92f8d` (WebTorrent 3.1.0-rc.3). Phase 2 commits below land proper Merkle-tree v2 + hybrid support.

## Shipped this session (2026-04-23)

| Commit | Sub-phase | Description |
|---|---|---|
| `43ab1db` | 2 foundation | `MerkleHasher` (pad-hash propagation formula, 16 KiB leaves) + this plan + 29 tests (incl. piece-size invariance). |
| `a621878` | 2a step 1 | v2 single-file creator + parser + `TorrentMetadata` schema bump (`MetaVersion`, `V2InfoHash`, `FileRoots`, `PieceLayers`) + `ByteArrayEqualityComparer` + `BencodeDecoder.DecodeDictionaryRawKeys` for binary-keyed dicts + 12 tests. |
| `1b15a7f` | 2a step 2 | `IncrementalMerkleHasher` (bounded-memory streaming Merkle tree) + v2 streaming path in `CreateFromStreamAsync` + 35 tests (byte-by-byte equivalence vs one-shot, chunk-size-invariance). |
| `a4b5e57` | 2a step 3 | v2 multi-file (in-memory, no alignment) + nested file tree + sorted piece layers + 11 tests. |
| `d7082c2` | 2b step 1 | Hybrid v1+v2 single-file. Combined info dict, two infohashes (SHA-1 + SHA-256). `TorrentCreatorOptions.Hybrid` option. 7 tests. |
| `178ee72` | 2b step 2 | Hybrid v1+v2 multi-file with pad files (`attr="p"`, `path=[".pad","N"]`) inserted between real files that don't end on piece boundaries. 6 tests. |
| `ddf2a32` | 2c step 1 | v2 magnet URI parsing (`xt=urn:btmh:1220<digest>`) + hybrid magnet. `Torrent.V2InfoHash` property + `ComputedMagnetUri` emits both. 12 tests. |

**Test totals:** 179/0/0 in full WebTorrent NUnit suite (68 pre-Phase-2 baseline + 111 new BEP 52 tests; zero regressions on the v1 path).

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

**Phase 2b status: SHIPPED** (except the HF proxy cutover, which is a separate follow-up commit).

**Explicit non-goals for 2b**
- No wire-protocol changes yet (the `bittorrent v2` extension negotiation isn't wired). A v2 peer only actually verifies with Merkle if it found the v2 infohash via a v2 magnet or a v2-aware .torrent file. See 2c.

### Phase 2c: Ecosystem + wire interop

**Deliverables**
- [x] `urn:btmh:` v2 magnet parsing (multihash-prefixed, SHA-256 variant). SHIPPED in commit `ddf2a32`.
- [ ] v2 peer protocol messages (BEP 52 §`Protocol extension`): types 21 (hash_request), 22 (hashes), 23 (hash_reject). Core peer wire, not BEP 10. Required so a v2-only magnet can fetch metadata and Merkle-verify pieces end-to-end.
- [ ] Merkle proof verification during piece download (`Torrent.Download.cs`): when a piece arrives, fetch any missing sibling hashes via hash_request, then verify the piece's leaf hashes compose up to the file's `pieces root`.
- [ ] Interop tests: generate a hybrid torrent with SpawnDev.WebTorrent, load it in libtorrent / qBittorrent, seed from one side to the other, verify both v1 and v2 peers fetch pieces correctly. Same in reverse: parse a libtorrent-generated v2 torrent and verify pieces.
- [ ] Documentation + example in `Docs/`.

**Estimated effort remaining:** 1-2 days on the wire extension + 1 day for interop tests against real external clients.

---

## Session closing state (2026-04-23)

Today's session shipped Phase 2a + 2b + 2c-step-1 end-to-end - 7 commits, 111 new tests, 0 v1 regressions, pushed to `origin/master`. See the shipped-commits table at the top.

## Remaining Phase 2 work (for future sessions)

1. **Phase 2c step 2: v2 peer wire messages.** BEP 52 reserves peer message types 21 (hash_request), 22 (hashes), 23 (hash_reject) on the core wire (not BEP 10). Implement encode/decode in `Wire.cs`, state machine for outstanding requests / timeouts, and integrate with `Torrent.Download.cs` piece verification so that on a piece arrival we fetch any missing sibling Merkle nodes and verify the piece's leaves compose up to the file's `pieces root`.

2. **Phase 2c step 3: external-client interop tests.** Generate a hybrid torrent with SpawnDev.WebTorrent, load it in `libtorrent` / qBittorrent, seed both ways and verify both v1 and v2 peers transfer correctly. Reverse: parse a libtorrent-generated v2 torrent and verify pieces. Largely manual / integration-tool work, hard to automate in CI without shipping test fixtures.

3. **HuggingFaceProxy v2 cutover.** Flip the model-torrent generator to `Hybrid = true` so CDN consumers get v2 Merkle verification without breaking any still-v1-only clients.

4. **PlaywrightMultiTest coverage for v2.** The NUnit tests under `SpawnDev.WebTorrent.Tests` verify the pure-CPU Merkle + bencode paths on desktop. Add a mirror of the core round-trip tests under `SpawnDev.WebTorrent.Demo.Shared/UnitTests/` so the v2 paths also run through `SpawnDev.UnitTesting` across all the browser profile targets, proving Blazor WASM behaves identically to desktop (per the crew rule on cross-platform test coverage).

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
