# Lazy-Hash Torrents — add a persistent torrent from web-seed URL(s), infohash unknown at add time

Source design: `2026-06-17_22-47_Notes.md` (+ `_16-00` / `_18-04`). This is the missing capability behind the
broken model delivery: there is no way to add a **persistent** torrent when all you have is a web-seed URL, so
consumers (the ILGPU.ML demo) fall back to a non-persistent `HttpRangeStream` that re-downloads every reload and
never caches/seeds. Lazy-Hash makes the model a real torrent from the first byte → cache + resume + restore + P2P.

## Goal (from the notes)
```csharp
var torrent = await Client.AddAsync("https://huggingface.co/.../model.onnx");
```
Add a single-file torrent whose **piece hashes + infohash are not yet known**; download from the web seed(s)
immediately, compute the hashes AS pieces arrive, finalize the .torrent when complete, then seed. Random-access
reads work during download (same as today). Caches to OPFS with resume; survives reload.

## What exists (reuse)
- `TorrentCreator.CreateFromUrlAsync(url)` — EAGER: probes the URL, then reads+hashes the WHOLE file before
  returning. Lazy-Hash is the deferred form: build the shell first, hash during download.
- `WebConn.cs` — web-seed (BEP 19) range fetch peer. Fetches piece bytes by HTTP range from a `url-list` seed.
  A web seed needs only URL+range — **no infohash required to fetch bytes.** This is what makes lazy possible.
- `Torrent.Download.cs` — block/piece assembly, `PutAsync` persist, `VerifyPieceHash`. Today it VERIFIES each
  piece against a KNOWN expected hash. Lazy mode must instead COMPUTE the hash (first downloader trusts the seed).
- `WebTorrentClient.RestoreFromStorageAsync` + `_state/{hash}.torrent` + `AsyncFSChunkStore` — persistence/restore,
  keyed by infohash. The keying is the main wrinkle (see below).
- `AddAsync(string magnetOrInfoHash)` — extend its input classification to also accept `http(s)://`.

## Design

### 1. Add path: classify + dedup
- `AddAsync`/`Add`: if input `StartsWith("http://"|"https://")` → Lazy-Hash path.
- Dedup: check existing torrents' web-seed `url-list` for this URL (and the hub-proxied form) → return existing.
- Optional hub fast-path (notes §22): ask known trackers/proxies (hub.spawndev.com) whether they already have a
  (possibly-incomplete) `.torrent` for this URL via the cacheing web-seed proxy; if so, adopt it (may still have
  zeroed tail hashes the client finishes). Else create fresh below. **Phase 2** — Phase 1 just uses the raw URL.

### 2. Build the torrent shell (infohash unknown)
- HEAD/Range-probe the URL: total size + `Accept-Ranges: bytes`. (No ranges ⇒ can't lazy-piece; fall back to a
  single whole-file fetch, or reject — decide in impl.)
- Pick piece length (same policy as `TorrentCreator`). Compute `pieceCount = ceil(size/pieceLen)`.
- File name from the URL path. `url-list` = [the URL] (+ hub proxy URL if a tracker offers it).
- Piece hashes array allocated but EMPTY/zeroed; `InfoHash`/`WireInfoHashHex` = empty (DisplayName already
  tolerates this — `bep52-example.md`: "Name ?? WireInfoHashHex ?? 'unknown'"). Mark torrent `LazyHash = true`.

### 3. Download (compute, don't verify)
- Drive `WebConn` against the web seed(s) like a normal download. Multiple seeds = parallel range fetches.
- On piece complete in LAZY mode: COMPUTE the piece hash (v1 SHA-1 and/or v2 leaf per chosen MetaVersion) from the
  assembled bytes, STORE it into the torrent's piece-hash array (instead of comparing to an expected one), persist
  the piece (`PutAsync`). A second seed disagreeing = a real corruption signal (optional cross-seed check, Phase 2).
- Trust model: the FIRST downloader trusts the seed and computes the hash. SUBSEQUENT downloaders who receive the
  finalized `.torrent` (real hashes) verify normally — unchanged existing path.

### 4. Finalize (hash known)
- When all pieces present + hashed: assemble the info dict, compute the real infohash (SHA-1 / SHA-256 per
  MetaVersion), set `InfoHash`/`WireInfoHashHex`, clear `LazyHash`, persist the now-complete `.torrent` to
  `_state/{infohash}.torrent`, and (if seeding configured) announce + seed to the P2P swarm.

### 5. Persistence keying BEFORE the hash is known (the main wrinkle)
- The store + `_state` are keyed by infohash, which is empty until finalize. Options:
  - (a) Key the provisional store + state by a stable PROVISIONAL id = hash of the canonical URL (e.g.
    `sha1("url:"+url)`), persist the partial `.torrent` + pieces under it, and on finalize MIGRATE/rename to the
    real infohash (or write a small alias record so restore can find it under either).
  - (b) Restore: `RestoreFromStorageAsync` already reads `_state/*.torrent`; a partial (lazy) `.torrent` carries the
    URL + size + any computed piece hashes + a `lazy` flag → restore resumes lazy download from where it left off
    (pieces present via `PieceExistsAsync`, remaining fetched from the seed). Provisional-keyed entries must be
    enumerated too.
- Decide (a) vs an alias map in impl. Keep restore O(metadata) (no whole-piece reads — the existing fix).

## Phased delivery (each phase PMT-green before the next)
- **Phase 1 — single-file lazy add + download + finalize + persist/restore**, raw URL as the only web seed.
  New tests (the lifecycle no test covers today): `AddAsync(url) → download fully → finalize → infohash matches a
  reference eager CreateFromUrl of the same bytes`; `download → fresh client restore → pieces present, ZERO
  re-download`; `random-access read mid-download`. Run in BOTH browser (OPFS) + desktop.
- **Phase 2 — hub proxy fast-path** (adopt the server's partial `.torrent`), multi-web-seed parallel fetch,
  cross-seed corruption check, seeding-on-complete to the swarm.
- **Phase 3 — wire ILGPU.ML delivery to `AddAsync(url)`** and DELETE the non-persistent `HttpRangeStream` fallback
  in `HubModelStream.OpenAsync`. The model is then always a persistent torrent (caches, restores, seeds).

## Why this fixes the reported bug
Re-download-every-refresh + empty cache page happen because the demo's web-seed fallback makes no torrent. With
`AddAsync(url)` the model is a torrent from byte 0 → `RestoreFromStorageAsync` finds it on reload (pieces in OPFS)
→ no re-download, shows on `/cache`, and seeds to other users.
