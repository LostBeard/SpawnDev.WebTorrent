# BEP 52 v2 Walkthrough

Concrete examples of creating, seeding, downloading, and interop-testing BitTorrent v2 (BEP 52) torrents with SpawnDev.WebTorrent.

> **Background:** BEP 52 introduces SHA-256 piece hashing with a Merkle tree over 16 KiB leaves instead of v1's flat SHA-1 per-piece list. Benefits: file-level integrity, per-block verification during streaming, larger pieces without loss of safety. **Hybrid v1+v2 torrents** carry both hash sets in one info dict so legacy (libtorrent 1.2 / qBittorrent 4.x) and modern (libtorrent 2.0+) clients can seed the same swarm. **Pure v2** torrents carry only the v2 tree and require a v2-aware client.

## Prerequisite: the flavors

| Flavor | `xt=` form | Parser sees | Info-hash | Use when |
|---|---|---|---|---|
| **v1-only** | `urn:btih:<40-hex-SHA1>` | `pieces` (flat SHA-1s) | v1 only | Back-compat with legacy ecosystem |
| **Pure v2** | `urn:btmh:1220<64-hex-SHA256>` | `file tree` + `piece layers` + `meta version=2` | v2 only | You control both ends, want per-block integrity |
| **Hybrid** | Both `xt=urn:btih:...&xt=urn:btmh:1220...` | Both sets in one info dict (plus pad files) | v1 AND v2 | You want everyone to be able to download |

## Creating each flavor

Single-file torrent from in-memory bytes — same API shape, different options:

```csharp
using SpawnDev.WebTorrent;

var data = File.ReadAllBytes("model.onnx");
var trackers = new[] { "wss://hub.spawndev.com:44365/announce", "wss://tracker.openwebtorrent.com" };

// v1-only (classic SHA-1)
var (v1Bytes, v1Meta) = TorrentCreator.CreateFromBytes("model.onnx", data,
    new TorrentCreatorOptions
    {
        PieceLength = 65536,
        Trackers = trackers,
        HashAlgorithm = "SHA-1",
    });

// Pure v2 (BEP 52 Merkle only)
var (v2Bytes, v2Meta) = TorrentCreator.CreateFromBytes("model.onnx", data,
    new TorrentCreatorOptions
    {
        PieceLength = 65536,
        Trackers = trackers,
        MetaVersion = 2,
        Hybrid = false,
    });

// Hybrid v1+v2 - recommended for general distribution
var (hybridBytes, hybridMeta) = TorrentCreator.CreateFromBytes("model.onnx", data,
    new TorrentCreatorOptions
    {
        PieceLength = 65536,
        Trackers = trackers,
        MetaVersion = 2,
        Hybrid = true,
    });

Console.WriteLine($"v1     infoHash = {v1Meta.InfoHash}");
Console.WriteLine($"v2     infoHash = {v2Meta.V2InfoHash}");
Console.WriteLine($"hybrid v1       = {hybridMeta.InfoHash}");
Console.WriteLine($"hybrid v2       = {hybridMeta.V2InfoHash}");
```

Multi-file works the same way via `TorrentCreator.CreateFromMultipleFiles` or, for streams larger than RAM (ML model shards, etc.), `TorrentCreator.CreateFromMultipleStreamsAsync` which is memory-bounded at ~1 piece + incremental Merkle state regardless of input size.

## Seeding

Pass the torrent bytes to the client and it'll serve whatever the peer asks for — v1 clients get v1 pieces, v2 clients get v2 pieces, hybrid clients pick whichever mode they negotiated via BEP 10 extension handshake:

```csharp
var client = new WebTorrentClient();
var torrent = await client.SeedAsync("model.onnx", data,
    new TorrentCreatorOptions
    {
        MetaVersion = 2,
        Hybrid = true,  // Reach every peer in the ecosystem
        Trackers = trackers,
    });

// Share the magnet URI (includes both v1 and v2 xt= for hybrid torrents).
Console.WriteLine(torrent.ComputedMagnetUri);
// magnet:?xt=urn:btih:<v1hex>&xt=urn:btmh:1220<v2hex>&dn=model.onnx&tr=...
```

## Downloading

Downloader side is even simpler - the client handles flavor detection automatically:

```csharp
var client = new WebTorrentClient();

// Any of these work - v1, pure-v2, or hybrid magnet
var magnet = "magnet:?xt=urn:btmh:1220abc...&dn=pure-v2-model";
var torrent = client.Add(magnet);

// Wait for metadata if starting from magnet (pieces are SHA-256-verified automatically
// via VerifyPieceHash → MerkleHasher.ComputePieceLayer for v2 / hybrid)
while (!torrent.HasMetadata) await Task.Delay(100);

Console.WriteLine($"MetaVersion: {torrent.MetaVersion}");        // 0 for v1-only, 2 for v2 / hybrid
Console.WriteLine($"WireInfoHashHex: {torrent.WireInfoHashHex}"); // What goes on the tracker wire
Console.WriteLine($"Display name: {torrent.DisplayName}");        // Name ?? WireInfoHashHex ?? "unknown"

// Wait for completion
while (!torrent.Done) await Task.Delay(1000);
```

## Integrity model (what SpawnDev.WebTorrent verifies)

Internally `Torrent.VerifyPieceHash(index, buf)` branches on `MetaVersion`:

- **`MetaVersion == 2`** (pure v2 or hybrid): the stored hash is the piece-layer Merkle root over the 16 KiB leaves inside the piece. `MerkleHasher.ComputePieceLayer(buf, PieceLength)` recomputes the single root for this piece from its leaves; matches against the stored 32-byte root. **Not** a flat SHA-256 of the whole piece - that would always mismatch.
- **`MetaVersion == 0`** (classic v1 or Phase 1 SHA-256): flat SHA-1 (20-byte stored hash) or flat SHA-256 (32-byte stored hash) over the whole piece. Auto-detected from the stored hash length.

Missing piece-layer hashes mid-download? The client fetches them via BEP 52 peer-wire messages 21 (`hash_request`), 22 (`hashes`), 23 (`hash_reject`). `V2HashRequestCoordinator` handles the state machine; `MerkleProofBuilder` on the seed side serves them.

## Pure-v2 identity on the wire

Pure-v2 torrents have `Torrent.InfoHash == ""` (no v1 hash). The client and trackers still need a 20-byte identity for the wire handshake + tracker announce. SpawnDev follows the libtorrent / qBittorrent / rqbit convention: use the **first 20 bytes of the v2 SHA-256 hash**. `Torrent.WireInfoHashHex` returns this automatically:

- v1 / hybrid → `WireInfoHashHex == InfoHash`
- pure-v2 → `WireInfoHashHex == V2InfoHash[..40].ToLowerInvariant()`

All SpawnDev storage (OPFS directory names, service-worker stream URLs, tracker RoomKey) keys on `WireInfoHashHex`, so pure-v2 torrents get a consistent 40-char identity end-to-end.

## Cross-client interop

Automated qBittorrent Web UI interop driver: `interop_test/qbittorrent_interop.cs`. Generates SpawnDev v1 + pure-v2 + hybrid torrents, adds them to a running qBittorrent instance via its REST API, force-rechecks pieces against a known payload, asserts 100% verification.

Run against qBittorrent 5.x (libtorrent 2.0):

```bash
cd interop_test
dotnet run gen_qbittorrent_test.cs       # produces output/*.torrent + payload.bin
dotnet run qbittorrent_interop.cs --host localhost --port 8080 --user admin --pass <yours>
```

Expected output:

```
qBittorrent 5.1.4 / libtorrent 2.0.10.0 / BEP 52 v2 capable: YES
  spawndev_v1:     PASS (v1-only SHA-1)
  spawndev_v2:     PASS (pure v2 SHA-256 Merkle)
  spawndev_hybrid: PASS (hybrid v1+v2)
```

Against qBittorrent 4.x (libtorrent 1.2) the script auto-skips pure-v2 (parser can't handle v2-only info dicts) and flags hybrid's missing v2 hash as an expected `(libtorrent 1.2 v1-only parse)` result rather than a mismatch. The v1 and hybrid-back-compat paths still pass.

## References

- [BEP 52 spec](http://bittorrent.org/beps/bep_0052.html)
- [libtorrent 2.0 release notes](https://www.libtorrent.org/upgrade_to_2.0.html)
- `Docs/bep52.md` — full end-state overview
- `Docs/bep-support.md` — BEP-by-BEP implementation status
- `Plans/PLAN-BEP52-External-Interop.md` — interop runbook + manual test steps
- `Plans/bep52-phase2-execution.md` — Phase 2 implementation history
