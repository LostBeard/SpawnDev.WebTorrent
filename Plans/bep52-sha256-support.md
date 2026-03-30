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

### Phase 1: SHA-256 Verification (our server-generated torrents)
- [ ] `TorrentCreator` — option to generate SHA-256 piece hashes (alongside or instead of SHA-1)
- [ ] `TorrentMetadata` — store SHA-256 piece hashes when present
- [ ] `PieceManager` — use SHA-256 verification when metadata has SHA-256 hashes
- [ ] `HuggingFaceProxy` — generate SHA-256 torrents for model delivery
- [ ] Already using `IPortableCrypto.Digest("SHA-256", data)` — same API, just a different string

### Phase 2: Full BEP 52 Compliance
- [ ] Merkle hash tree for piece verification
- [ ] Per-file piece alignment in torrent creation
- [ ] Hybrid v1+v2 info dict for backwards compatibility
- [ ] Parse and serve both v1 and v2 torrents from other clients

### Phase 3: Ecosystem
- [ ] Interop testing with libtorrent/qBittorrent v2 torrents
- [ ] Magnet URI v2 format (`urn:btmh:` multihash)

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
