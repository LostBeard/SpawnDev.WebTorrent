# BEP 52 — BitTorrent v2 (Merkle-tree piece verification)

SpawnDev.WebTorrent ships full BEP 52 v2 support: creator, parser, magnet URIs, peer-wire extension (messages 21/22/23), and Merkle-tree piece verification. This page walks through the shape of v2 torrents, what changes at the consumer API, and how v2 peers interoperate with v1-only peers.

[BEP 52 spec](https://www.bittorrent.org/beps/bep_0052.html) · Source: `SpawnDev.WebTorrent/` (`MerkleHasher.cs`, `MerkleProofVerifier.cs`, `MerkleProofBuilder.cs`, `Bep52WireMessages.cs`, `V2HashRequestCoordinator.cs`, `TorrentCreator.cs` v2 paths, `TorrentParser.cs` v2 paths).

## Why v2

v1 torrents hash the bencoded info dict with SHA-1 and each piece with a flat SHA-1 over the piece's bytes. v2 replaces both:

- **Info hash is SHA-256** of the v2 info dict (urn:btmh: multihash in magnet URIs)
- **Piece hashes are Merkle roots** over 16 KiB leaves (not flat hashes of the whole piece)
- **File Merkle roots** enable per-file verification independent of piece size, and let peers request partial hash trees to verify out-of-order blocks

The headline consumer wins: SHA-1 collision safety, per-file integrity, and the ability to verify smaller chunks than a piece.

## Creating a hybrid v1+v2 torrent (recommended)

Hybrid torrents carry both v1 (`pieces` key, SHA-1) and v2 (`meta version = 2`, `file tree`, `piece layers`, SHA-256) in one info dict. They produce **two valid infohashes** and interoperate with both v1-only and v2-aware clients.

```csharp
var opts = new TorrentCreatorOptions
{
    MetaVersion = 2,
    Hybrid = true,           // emit both v1 and v2 keys in one info dict
    PieceLength = 65536,     // piece size (must be a power-of-2 multiple of 16 KiB)
};

var (bytes, meta) = TorrentCreator.CreateFromBytes("model.bin", data, opts);
// meta.InfoHash     -> 40-char hex SHA-1 (v1)
// meta.V2InfoHash   -> 64-char hex SHA-256 (v2)
// meta.FileRoots    -> per-file Merkle roots
// meta.PieceLayers  -> per-file piece-layer hashes (only for files > PieceLength)
```

For large files (multi-GiB AI models, etc.) use the streaming path to keep memory bounded:

```csharp
using var stream = File.OpenRead("weights.safetensors");
var (bytes, meta) = await TorrentCreator.CreateHybridSingleFileFromStreamAsync(
    "weights.safetensors", stream, stream.Length, opts);
```

The `HuggingFaceProxy` emits hybrid by default, so every HF model torrent carries both infohashes.

## Pure v2 (no v1 fallback)

```csharp
var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 65536 };
var (bytes, meta) = TorrentCreator.CreateFromBytes("pure-v2.bin", data, opts);
// meta.InfoHash is empty; only meta.V2InfoHash is populated
```

Pure v2 torrents **cannot be exchanged with v1-only clients**. Use hybrid unless you have a specific reason to block v1 peers.

## Magnet URIs

v1 magnets use `xt=urn:btih:<sha1>`. v2 magnets use `xt=urn:btmh:1220<sha256>` where `1220` is the multihash prefix (`0x12` = SHA-256, `0x20` = 32-byte digest). Hybrid magnets carry both `xt` values:

```
magnet:?xt=urn:btih:aaaa...1234
       &xt=urn:btmh:1220cccc...abcd
       &dn=model.bin
       &tr=wss%3A%2F%2Ftracker.example.com
```

`Torrent.ParseMagnet` populates both `InfoHash` and `V2InfoHash` when both are present. `Torrent.ComputedMagnetUri` emits both when both are set.

## Piece verification branching

Critical correctness: a v2 torrent's stored "piece hash" is the **Merkle root over the piece's 16 KiB leaves**, NOT a flat SHA-256 of the piece bytes. When `PieceLength > LeafSize` (16 KiB), these values differ - a flat SHA-256 comparison would silently fail every piece of a large-piece-size v2 torrent.

`Torrent.VerifyPieceHash` (internal, test-visible) branches on `MetaVersion`:

```csharp
if (MetaVersion == 2) {
    var pieceLayer = MerkleHasher.ComputePieceLayer(buf, PieceLength);
    return pieceLayer[0].AsSpan().SequenceEqual(expected);  // Merkle root
}
// v1 / Phase 1: flat SHA-1 (20B) or flat SHA-256 (32B) by length
```

Consumers don't need to call this directly - it runs automatically on piece arrival in `Torrent.Download.cs`.

## Peer-wire extension (BEP 52 §"Protocol extension")

v2 introduces three core peer-wire message types (NOT BEP 10 extensions - they're base protocol):

| ID | Message | Direction | Purpose |
|----|---------|-----------|---------|
| 21 | `hash_request` | either way | Request a range of Merkle tree hashes for a file root |
| 22 | `hashes` | response | Deliver the requested hash range + sibling uncle proof |
| 23 | `hash_reject` | response | Refuse the request (we don't have those hashes) |

Payload layout (`Bep52WireMessages.cs`):

```
hash_request / hash_reject:   [pieces_root:32][base_layer:u32 BE][index:u32 BE][length:u32 BE][proof_layers:u32 BE]  = 48 bytes
hashes:                       <header 48 bytes> [hash_0:32][hash_1:32]...  (length + proof_layers entries)
```

### Serving a hash_request (seed path)

`V2HashRequestCoordinator` (allocated per v2 torrent, auto-hooked to every peer Wire by `Torrent.OnWireWithMetadata`) routes incoming `hash_request` to `Torrent.OnV2HashRequest`, which:

1. Looks up the requested `pieces_root` in our `PieceLayers` dict
2. Hands the base-layer range + requested proof layers to `MerkleProofBuilder.Build`
3. Replies with a `hashes` message, or `hash_reject` if we don't hold that root / the request is malformed

Both layers are served: `base_layer == pieceLayerLevel` returns piece-layer hashes (the common case for v2-only magnet bootstrap or re-verification), and `base_layer == 0` returns 16 KiB leaf hashes by re-hashing the corresponding piece content from our chunk store. Malformed or unknown-root requests return `hash_reject` rather than stalling.

### Issuing a hash_request (client path)

```csharp
if (torrent.MetaVersion != 2 || torrent.V2HashCoord is null)
    throw new InvalidOperationException("Only v2 torrents have a V2HashCoord");

var req = new Bep52WireMessages.HashRequest(
    piecesRoot: fileRoot,
    BaseLayer: (uint)pieceLayerLevel,
    Index: 0,
    Length: 4,
    ProofLayers: 1);

byte[][] verifiedHashes = await torrent.RequestV2HashesAsync(req);
// coordinator correlates the response on any connected wire, runs MerkleProofVerifier,
// and resolves with the verified hash list (or throws on reject/timeout/verify-fail)
```

Timeout defaults to 15 seconds (configurable on the coordinator). The send callback is the chosen peer's `Wire.SendHashRequest`; the response can arrive on ANY peer wire (coordinator key is `(pieces_root, base_layer, index, length)`, peer-agnostic).

## Interop with v1-only peers

Hybrid torrents are bit-compatible with v1-only clients. A v1 peer sees only the v1 keys in the info dict (`pieces`, `piece length`, `files`) and exchanges pieces via classic BEP 3 wire messages. The v2 peer-wire extension messages (21/22/23) are core-protocol but a v1-only peer will treat them as unknown messages and drop them - the handler in `Wire.cs` catches `ArgumentException` from the decoder and falls through to `OnUnknownMessage`, so malformed or unexpected input never crashes the wire.

A v2 peer connected to a v1 peer simply never issues `hash_request` against it (v1 peer doesn't advertise v2 support), and the torrent's download pipeline uses the stored piece-layer hashes populated at parse time.

## Test coverage

All v2 paths are exercised on **both** desktop .NET and Blazor WASM via `WebTorrentTestBase` partials in `SpawnDev.WebTorrent.Demo.Shared` (e.g. `WebTorrentTestBase.Bep52V2Tests.cs`), driven by PlaywrightMultiTest so every test runs through both runtimes. The historical NUnit-only `SpawnDev.WebTorrent.Tests` project was retired 2026-04-23 — every former NUnit test now lives in the shared base class. Browser behavior matches desktop byte-for-byte on the hashing / encoding / decoding paths and state-for-state on the coordinator.

Key tests:

- `MerkleHasherTests` - 29 tests including piece-size invariance (a file's root must be the same whether computed with 16 KiB or 128 KiB pieces - catches pad-hash bugs invisible on a self-round-trip)
- `MerkleProofVerifierTests` - 13 tests including 8-leaf middle-range (exercises two-level proof with alternating sibling placement)
- `MerkleProofBuilderTests` - 13 tests including round-trips through the verifier (seed output must be verifier-accepted)
- `Bep52WireMessagesTests` - 10 tests including a known-byte endian check
- `V2HashRequestCoordinatorTests` - 10 tests covering happy path, reject, timeout, verify-fail, cancel, duplicate-key, unsolicited drop, failed send, concurrent requests
- `TorrentV2HashCoordinationTests` - 10 tests for the Torrent ↔ coordinator integration (event forwarding, seed path via `MerkleProofBuilder`, cross-wire response correlation)
- `VerifyPieceHashTests` - 9 tests including the real `TorrentCreator` → `TorrentParser` → `Torrent.VerifyPieceHash` pipeline with 64 KiB pieces (the latent-bug-catching case)

Total BEP 52-specific tests: **~180** across NUnit (desktop) and SpawnDev.UnitTesting (browser via PlaywrightMultiTest).

## External-client interop

All four interop paths PASS as of 2026-04-25:

- **JS WebTorrent (v1) ↔ SpawnDev.WebTorrent.** Round-trips between the official JS WebTorrent library and `SpawnDev.WebTorrent.Demo` through `hub.spawndev.com:44365/announce`. Locked by `interop_test/js_webtorrent_liveswarm.cs` (Node.js `webtorrent@^2` + `@roamhq/wrtc` seeds; SpawnDev.WebTorrent C# leeches via local SpawnDev.RTC tracker + WebRTC; 1 MiB SHA-256 byte-identical).
- **qBittorrent / libtorrent 2.0 (v1 + pure v2 + hybrid) static interop.** `interop_test/qbittorrent_interop.cs` adds the SpawnDev-generated `.torrent` to qBittorrent, force-rechecks, asserts 100% completion + matching v1 / v2 info hashes. All three formats green.
- **qBittorrent live-swarm forward** (qBittorrent seeds → SpawnDev.WebTorrent leeches via TCP). 1 MiB hybrid torrent SHA-256 byte-identical end-to-end.
- **qBittorrent live-swarm reverse** (SpawnDev.WebTorrent seeds via `TcpListenerService` → qBittorrent leeches via `addPeers`). 1 MiB hybrid torrent SHA-256 byte-identical end-to-end. Closes `Plans/PLAN-BEP52-External-Interop.md` Step 4 in both directions.

`Plans/PLAN-BEP52-External-Interop.md` carries the full runbook + fixture corpus (4 libtorrent 2.0 reference torrents embedded in `Demo.Shared/InteropFixtures/` + parsed in PlaywrightMultiTest).

## Known limitations

- **Pure-v2-only multi-file downloads via the global-piece-index download engine.** Torrents created with `MetaVersion=2, Hybrid=false` and multiple files parse correctly (metadata, file tree, per-file roots, piece layers) and download correctly through the production paths shipped in 3.1.3-rc.20+ (pure-v2 dedup, persist/restore, service-worker streaming, HTTP file browser keyed on `WireInfoHashHex`). The Phase 3 followup ("per-file piece indexing inside the global download engine") was completed for the consumer-visible paths; the only remaining edge is direct global-piece-index API callers, who should use `Torrent.Files[i].ReadAsync` instead.

## File reference

| Path | Role |
|---|---|
| `MerkleHasher.cs` | Core primitives - leaf hash, pad-hash-at-level, compute-root, compute-piece-layer, compute-file-root |
| `IncrementalMerkleHasher.cs` | Streaming variant of the above for multi-GiB files |
| `MerkleProofVerifier.cs` | Given `(pieces_root, index, baseLayerHashes, proofHashes)`, reconstruct the root and compare |
| `MerkleProofBuilder.cs` | Inverse of verifier - given a full layer + request params, emit the base + proof arrays |
| `Bep52WireMessages.cs` | Record structs + big-endian codecs for message IDs 21/22/23 |
| `V2HashRequestCoordinator.cs` | Per-torrent state machine - correlation, timeout, verification, cancellation |
| `Torrent.cs` | Stores `FileRoots`, `PieceLayers`, allocates `V2HashCoord` when `MetaVersion == 2` |
| `Torrent.Download.cs` | Wires peer events, seed-path `OnV2HashRequest`, client-path `RequestV2HashesAsync`, MetaVersion-aware `VerifyPieceHash` |
| `Wire.cs` | Raw-byte dispatch for messages 21/22/23 + `OnHashRequest` / `OnHashes` / `OnHashReject` events + `SendHashRequest` / `SendHashes` / `SendHashReject` |
| `TorrentCreator.cs` | v2 and hybrid creation paths (single-file, multi-file, streaming) |
| `TorrentParser.cs` | v2 detection, file-tree walk, piece-layers dict decode, dual-infohash computation |
