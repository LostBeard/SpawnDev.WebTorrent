# BEP 52 Phase 2 Execution Plan

**Goal:** Ship full BEP 52 v2 compliance in SpawnDev.WebTorrent: Merkle-tree piece verification, per-file piece alignment, hybrid v1+v2 info dicts, and bidirectional interop with `libtorrent` / qBittorrent v2 torrents.

**Current state (2026-04-23):** BEP 52 Phase 1 shipped in commit `de92f8d` (WebTorrent 3.1.0-rc.3). That's SHA-256 piece hashes for server-generated torrents - a flat list of 32-byte hashes, not real BEP 52. Real v2 requires Merkle trees.

This plan scopes Phase 2 into three shippable sub-phases so each lands as a working, tested, committed release candidate rather than one multi-week megacommit.

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

**Explicit non-goals for 2a**
- No hybrid v1+v2 torrents yet (pure v2 only).
- No v2 magnet URI (`urn:btmh:`) parsing yet.
- No `ut_hash_request` extension yet (no partial-tree fetch over the wire).

**Estimated effort:** 1-2 focused days. Merkle algorithm is subtle (padding, per-piece vs per-file roots); interop test vectors have to be correct or we'll silently ship broken torrents.

### Phase 2b: Per-file alignment + hybrid v1+v2 info dict

**Deliverables**
- `TorrentCreator` hybrid path: single pass over input files produces BOTH v1 flat SHA-1 hashes AND v2 Merkle tree. Emits a single info dict with both `pieces` (v1) and `meta version`/`file tree`/`piece layers` (v2). Two valid infohashes.
- Per-file piece alignment: each file starts at a piece boundary; pad with zeros between files so v1 piece hashes still compute correctly over a continuous stream. Required by BEP 52 for hybrid (v1 and v2 must see the same piece content).
- `TorrentParser` hybrid detection: when both sets of fields are present, surface both infohashes; consumer picks which swarm to join.
- `HuggingFaceProxy` generator switches to hybrid output once 2b lands, so existing v1-only clients keep working while v2 clients get Merkle verification.

**Explicit non-goals for 2b**
- No wire-protocol changes yet (the `bittorrent v2` extension negotiation isn't wired). A v2 peer only actually verifies with Merkle if it found the v2 infohash via a v2 magnet or a v2-aware .torrent file.

**Estimated effort:** 1-2 days on top of 2a.

### Phase 2c: Ecosystem + wire interop

**Deliverables**
- `urn:btmh:` v2 magnet parsing (multihash-prefixed, SHA-256 variant).
- `ut_hash_request` wire extension (BEP 52 §`Protocol extensions`) to fetch missing Merkle nodes from v2-capable peers.
- Interop tests: generate a hybrid torrent with SpawnDev.WebTorrent, load it in libtorrent / qBittorrent, seed from one side to the other, verify both v1 and v2 peers fetch pieces correctly. Same in reverse: parse a libtorrent-generated v2 torrent and verify pieces.
- Documentation + example in `Docs/`.

**Estimated effort:** 1-2 days on top of 2b, largely test-and-iterate against real external clients.

---

## Today's session target

**Land Phase 2a foundation:**
1. This plan doc (shipped now as a standalone commit or bundled with the first Merkle code).
2. `MerkleHasher` class with full unit tests (known-answer tests, zero-pad cases, multi-piece files, single-piece files, empty file edge case).
3. Optional stretch: Begin v2 info-dict serialization in `TorrentCreator` if time allows.

**Not today:** parser changes, `TorrentMetadata` schema bump, `Torrent.Download.cs` verification changes. Those come in a follow-up commit once the hasher is proven.

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
