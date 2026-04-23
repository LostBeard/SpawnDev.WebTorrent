# BEP 52 External-Client Interop Tests (Phase 2c step 3)

Phase 2c steps 1 + 2 shipped self-consistent SpawnDev BEP 52 v2: create a hybrid torrent with `TorrentCreator`, parse it with `TorrentParser`, exchange pieces over the WebRTC wire between two SpawnDev peers, verify the v2 Merkle chain all the way to the file root. That proves our implementation is internally coherent. It does NOT prove we interoperate with libtorrent / qBittorrent / transmission / the rest of the v2 ecosystem.

This plan scopes the interop verification. The work is largely manual (installing external clients, setting up cross-process testbeds, comparing byte outputs) so it's hard to automate in CI without shipping binary fixtures. But each step produces a checkable artifact that proves a specific compatibility claim.

## Scope

Three directions to verify, each both ways:

1. **SpawnDev → libtorrent:** SpawnDev-created `.torrent` loads + seeds + fully-downloads in libtorrent.
2. **SpawnDev → qBittorrent:** Same via qBittorrent's GUI.
3. **libtorrent → SpawnDev:** libtorrent-created v2 `.torrent` loads + parses + verifies pieces in SpawnDev.WebTorrent.

Plus two cross-seed scenarios:

4. SpawnDev seeds, libtorrent downloads — piece-by-piece exchange.
5. libtorrent seeds, SpawnDev downloads — piece-by-piece exchange.

## Prerequisites

- **libtorrent** ≥ 2.0 (first version with full v2 support). Build locally or install via OS package manager. The `libtorrent-rasterbar` library from Arvid Norberg; Python bindings are convenient for scripted tests but the `make_torrent` example binary is sufficient.
- **qBittorrent** v4.4+ (paired with libtorrent 2.0+).
- **Local tracker** — we already run one (`SpawnDev.WebTorrent.ServerApp` on port 5560). Interop tests MUST announce both clients to the same tracker so they find each other. A v2-only torrent needs peers exchanging v2 info hashes, which the tracker relays transparently (tracker doesn't care about v1 vs v2).

## Step breakdown

### Step 1: Byte-level torrent-file diff against libtorrent reference

**Deliverable:** Script that:
1. Runs libtorrent's `make_torrent` example to produce a v2 torrent file for a deterministic input (e.g., 10 MB of zero bytes, or a specific `/dev/urandom` seed).
2. Runs `TorrentCreator.CreateFromBytes("same.bin", same_data, { MetaVersion=2 })` with matching inputs.
3. Decodes both `.torrent` files (bencode) and structurally diffs the info dicts.
4. Reports any key-level or value-level disagreement.

Expected discrepancies:
- `creation date`, `created by` — benign, different per run / per client.
- `announce-list` field — benign, ordering / array shape may differ but libtorrent's round-trip tolerates both.

Unexpected discrepancies (would be real bugs to fix):
- Any difference in `info.file tree` shape or per-file Merkle roots.
- Any difference in `piece layers` dict contents.
- Any difference in `info.meta version`, `info.name`, `info.piece length`.
- v2 info hash mismatch (SHA-256 of info dict).

Success criterion: after excluding the benign fields, two independently-generated v2 torrents of the same content produce **byte-identical info dicts** → byte-identical v2 info hashes. This is the ultimate interop proof: libtorrent and SpawnDev compute the same hash from the same bytes.

### Step 2: SpawnDev-created torrent loads in qBittorrent

**Manual test plan:**
1. `TorrentCreator.CreateFromBytes` → save output to `test.torrent`.
2. Drag `test.torrent` onto qBittorrent.
3. qBittorrent should show the file list, total size, piece count, and **two infohashes** (the hybrid v1 + v2). If it only shows v1, the v2 keys aren't being recognized. If it shows neither, the file is malformed.
4. Start seeding. Confirm qBittorrent announces to our tracker and the hash comparison on the tracker side sees it as a valid swarm.

**Captured as a test fixture:** save the `.torrent` output into `SpawnDev.WebTorrent.Tests/TestData/hybrid-v2-sample.torrent` and commit it. Future regressions in the creator can be caught by re-loading this known-good file in parsing tests AND periodically re-verifying it in qBittorrent (manual).

### Step 3: libtorrent-created torrent parses in SpawnDev.WebTorrent

**Automated test plan:**
1. Run `libtorrent-make-torrent --v2 --piece-size 65536 payload.bin` → `libtorrent-output.torrent`.
2. Commit that file as `SpawnDev.WebTorrent.Tests/TestData/libtorrent-v2-sample.torrent` (size TBD; keep payload small for git).
3. `TorrentParser.Parse(File.ReadAllBytes(...))` — should succeed, populate `MetaVersion=2`, `V2InfoHash=<expected SHA-256>`, `FileRoots=[...]`, `PieceLayers=dict(...)`.
4. `Torrent.VerifyPieceHash(i, pieceData)` should accept every piece from the original `payload.bin`.

NUnit test in `SpawnDev.WebTorrent.Tests`:

```csharp
[Test]
public void LibtorrentV2Torrent_Parses_AndVerifiesPieces()
{
    var torrentBytes = File.ReadAllBytes("TestData/libtorrent-v2-sample.torrent");
    var parsed = TorrentParser.Parse(torrentBytes);
    Assert.That(parsed.MetaVersion, Is.EqualTo(2));
    Assert.That(parsed.V2InfoHash, Is.EqualTo("<expected SHA-256 hex>"));
    // ... verify piece count, file count, piece layers dict, etc.
}
```

### Step 4: End-to-end cross-seeding

**Most valuable but hardest test.** Requires two processes talking to a live tracker simultaneously.

Option A - fully local:
- Run `SpawnDev.WebTorrent.ServerApp` locally (tracker on port 5560).
- Run SpawnDev.WebTorrent's WpfDemo as seeder with a known payload.
- Run qBittorrent with the same `.torrent`, configure to use `http://localhost:5560/announce`.
- Verify qBittorrent downloads to completion. Hash the result, confirm byte-identical to the seeder's input.
- Reverse: qBittorrent seeds, WpfDemo downloads. Hash-verify.

Option B - library-level:
- Integration test using libtorrent's Python bindings to drive a libtorrent peer in-process alongside a SpawnDev.WebTorrent peer. Both connect to a test tracker instance. Assert both reach 100% and final bytes hash-match.

Option A is more realistic (exercises real UDP / HTTP / WebSocket tracker paths); Option B is more reproducible in CI.

### Step 5: Ship a binary-compatibility test fixture bundle

**Deliverable:** `SpawnDev.WebTorrent.Tests/TestData/interop/` folder with:
- A few reference v2 torrents generated by libtorrent at common piece sizes (16 KiB / 64 KiB / 1 MiB).
- The corresponding raw payload files (kept small, a few KB each to keep git friendly).
- A README explaining provenance and regeneration command.

NUnit tests parse each fixture and verify structural expectations. Future changes to our parser that break compatibility with the reference corpus fail loud instead of drifting.

## Why this is "hard to automate in CI"

- External binaries (libtorrent, qBittorrent) aren't typically on CI runners. Either we ship them as test dependencies (heavy) or the fixture approach (cheap but only verifies parse-compat, not live-swarm-compat).
- Cross-process trackerful tests need real network binding and announce round-trips. Works great locally on TJ's dev box; fragile on shared CI.
- qBittorrent drag-and-drop testing is inherently manual unless we automate a GUI driver (AutoHotkey / pyautogui) which is its own rabbit hole.

**Recommended CI posture:** ship step 5 (fixture corpus) in CI. Steps 1-4 stay as local-dev / pre-release smoke tests, documented here so anyone picking them up has a runbook.

## Success checklist

- [ ] Step 1: byte-level info-dict diff against libtorrent reference → info hashes match on matched inputs.
- [ ] Step 2: qBittorrent shows both hashes + correct metadata when loading a SpawnDev-generated hybrid torrent.
- [ ] Step 3: libtorrent-generated v2 torrent parses correctly + every piece verifies against our `Torrent.VerifyPieceHash`.
- [ ] Step 4: End-to-end cross-seeding in both directions (SpawnDev↔qBittorrent; SpawnDev↔libtorrent) reaches 100% completion, hash-verifies.
- [ ] Step 5: `TestData/interop/` committed with README + at least 3 reference fixtures + parse tests passing.

## Estimated effort

- Step 1 (scripted diff): 1 day (Python + libtorrent bindings + bencode decoder we already have in C#)
- Step 2 (manual qBittorrent check + fixture capture): 2 hours
- Step 3 (parse + verify reference libtorrent output): 2 hours (once step 5 fixtures exist)
- Step 4 (E2E cross-seeding): 1-2 days (setup + iteration on tracker / port / client-options)
- Step 5 (fixture corpus + docs): 1 day

**Total:** ~3-4 days of focused work, across 1-2 sessions. Primary blocker is access to a machine with libtorrent + qBittorrent installed and a test tracker running.

## Rule alignment

- **Rule 1 (last release):** Each step is shippable on its own. Step 3 alone is a meaningful compat gain (we can consume libtorrent output). Step 4 in just one direction (SpawnDev→external) is a meaningful win.
- **Rule 2 (fix libraries first):** If interop testing surfaces a BEP 52 spec deviation, the fix lands in our core library — not in a compatibility shim. Same as Phase 2a/2b.
- **Rule 4 (performance):** Not a performance focus; correctness-focused.
- **Rule 5 (real tests):** Fixture corpus IS real data from a real external client. The E2E cross-seed IS real swarm exchange.

## Handoff note

Whoever picks this up:

1. Read `Docs/bep52.md` for the v2 shape we implement.
2. Read `Docs/bep-support.md` BEP 52 row for current status.
3. Install libtorrent 2.0+ on dev box. On Windows the easiest path is vcpkg: `vcpkg install libtorrent[python]`.
4. Install qBittorrent v4.4+.
5. Start with step 1 (byte-diff). If that passes, step 3 is cheap. Steps 2 and 4 are the manual-testing components.
6. Ping Captain before committing any binary fixtures > 1 MB.
