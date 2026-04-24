# qBittorrent Interop Testing

This document describes how SpawnDev.WebTorrent's BEP 52 v2 output is validated against a real qBittorrent client, why this test matters, and how to run / extend the harness.

## Why this exists

SpawnDev.WebTorrent writes torrents in three formats:
- **v1** (BEP 3) - SHA-1 piece hashes, classic BitTorrent.
- **pure v2** (BEP 52) - SHA-256 Merkle-tree piece hashes, no v1 piece list.
- **hybrid v1+v2** - both encodings in one `info` dict, two infohashes.

Every one of those has to interop with real-world BitTorrent clients. qBittorrent + libtorrent is the industry reference for both v1 and v2 (qBittorrent 4.4+ on libtorrent 2.0+). If our torrents don't load cleanly there, they don't load anywhere.

## What the harness proves (and what it doesn't)

### Static interop (`qbittorrent_interop.cs`) - shipped

For each of the three formats, automates:
1. Adding the SpawnDev-generated .torrent to qBittorrent's client (via Web UI REST API).
2. Copying the matching `payload.bin` into qBittorrent's configured save path.
3. Force-rechecking.
4. Confirming 100% piece completion.
5. Cross-checking qBittorrent's reported `infohash_v1` / `infohash_v2` against hashes we computed ourselves.

**Proves:** qBittorrent parses the bencoded `info` dict the same way we do, hashes the pieces the same way we do, and byte-matches our hashes. Binary compatibility at the file level.

**Does NOT prove:** peer-wire exchange over a live network. That's the live-swarm step, WIP.

### Live-swarm interop (`qbittorrent_liveswarm.cs`) - PASSING

Proves that bytes actually flow between qBittorrent (seeder) and a SpawnDev.WebTorrent C# client (downloader) over a real TCP BitTorrent peer-wire. Flow:
1. Web UI auth + read `listen_port` + `save_path`.
2. Delete any stale payload.bin torrents from previous runs (releases qBittorrent's file-map hold).
3. Copy `interop_test/output/payload.bin` into qBittorrent's save_path.
4. Add `spawndev_hybrid.torrent` with `skip_checking=true`, force-recheck, poll `/api/v2/torrents/info` until `progress==1.0` and state is `stalledUP`/`seeding` - qBittorrent is now seeding.
5. Create a SpawnDev.WebTorrent client (no tracker announce, no AsyncFileSystem - MemoryChunkStore).
6. Add the same `.torrent`.
7. Open a direct TCP connection to `127.0.0.1:<listen_port>` via `TcpPeer.ConnectAsync`, retry up to 5x (qBittorrent's listener briefly cycles during add+recheck).
8. `torrent.AddPeer(tcpPeer)` → BT handshake → piece exchange.
9. Wait for `torrent.Done`.
10. Pull bytes from the in-memory chunk store via `torrent.Files[0].ReadAsync(0, Length)`, SHA-256-compare against the original `payload.bin`.

Last green run: **1 MiB transferred, SHA-256 match** in under 3 seconds on localhost.

### Library bug found by this test

`Torrent.AddPeer(SimplePeer)` only subscribed to `OnConnect`; if the underlying peer had already transitioned to `Connected` before `AddPeer` was called (as happens for `TcpPeer` where `ConnectAsync` fires `EmitConnect` synchronously before returning), the subscription came in too late and `Peer.OnConnected` never fired, which means no BT handshake, no wire, no transfer. Affects every direct-add caller - including the production `Torrent.ConnectTcpPeer` path that discovery/LSD peer discovery ultimately funnels into.

Fix: `AddPeer` now captures the wire-up in a local `runOnConnected` delegate, subscribes it to `OnConnect` for the normal case, AND invokes it inline immediately if `simplePeer.Connected` is already true. Landed in `Torrent.cs` along with this doc.

## Setup

### One-time qBittorrent configuration

1. **Enable Web UI.** Tools → Preferences → Web UI → Enable. Default port `8080`, default username/password `admin` / `adminadmin` (change or keep for dev/test - the harness defaults to these).
2. **Note the TCP listen port.** Tools → Preferences → Connection → Port used for incoming connections. On a fresh install this is a random high port (we've seen `19141` on TJ's machine). The live-swarm script reads this from `/api/v2/app/preferences` automatically.

Run once:

```bash
cd D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\interop_test
dotnet run gen_qbittorrent_test.cs
```

This generates `output/payload.bin` (1 MiB deterministic content) plus three `.torrent` files (`spawndev_v1.torrent`, `spawndev_v2.torrent`, `spawndev_hybrid.torrent`). The payload uses a fixed seed so hashes are deterministic across runs.

### Per-test-run (both tests)

The qBittorrent Web UI must be running + reachable. Both test scripts default to `http://localhost:8080` with credentials `admin` / `adminadmin`. Override via env vars:

| Env var | Default | Meaning |
|---------|---------|---------|
| `QBT_HOST` | `localhost` | Web UI host |
| `QBT_PORT` | `8080` | Web UI port |
| `QBT_USER` | `admin` | Web UI login |
| `QBT_PASS` | `adminadmin` | Web UI password |

Or pass via CLI: `--host`, `--port`, `--user`, `--pass`.

### Running the tests

```bash
cd D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\interop_test

# Static binary-compat test (v1 + pure-v2 + hybrid all force-rechecked to 100%).
dotnet run qbittorrent_interop.cs

# WIP live-swarm test (qBittorrent seeds, our C# client downloads over localhost TCP).
dotnet run qbittorrent_liveswarm.cs
```

Both scripts are **.NET 10 single-file scripts** (they use `#:project` to pull in the SpawnDev.WebTorrent library directly). No csproj, no NUnit harness - just `dotnet run` on the `.cs` file. Keeps the interop tests divorced from the main test project so they don't run in CI / PlaywrightMultiTest.

Each script exits 0 on success, non-zero on failure. Stdout has human-readable progress.

## Multi-qBittorrent setup (for live-swarm expansion)

qBittorrent can be run as **multiple simultaneous instances** on one box, each with its own Web UI port, TCP listen port, and save path. This is what makes the multi-seeder / multi-peer live-swarm harness tractable - we can stand up 2 or 3 qBittorrent peers and remote-control them all via the same Web UI REST driver.

### How to launch a second qBittorrent instance on Windows

```powershell
# Default instance stays where it is.
# Launch a second instance with a separate profile dir:

& "C:\Program Files\qBittorrent\qbittorrent.exe" ^
    --profile="C:\Users\TJ\AppData\Local\qBittorrent-instance2" ^
    --webui-port=8082
```

Key flags:
- `--profile=<dir>` - separate BT_Backup / preferences / session from the main install. REQUIRED for concurrent instances.
- `--webui-port=<port>` - use a port other than the first instance's 8080. 8082 is conventional.

Inside the second instance, set a distinct TCP listen port (e.g. 19142 vs the default instance's 19141) and a distinct save path. That avoids save-dir collisions when both are torrenting the same payload.

### Controlling both from the harness

The existing `qbittorrent_interop.cs` takes `--host` / `--port` / `--user` / `--pass` so running it twice against different `--port` values drives both instances independently. A multi-instance harness would just instantiate the same `HttpClient` + cookie pattern for each instance.

Pattern:
```csharp
var a = new QbtDriver("localhost", 8080, "admin", "adminadmin");
var b = new QbtDriver("localhost", 8082, "admin", "adminadmin");
await a.Login(); await b.Login();
// A seeds, B downloads, both announce to our tracker ...
```

That wrapper doesn't exist yet - the current scripts are self-contained. Writing a shared `QbtDriver` when we graduate the live-swarm test.

## What's in `interop_test/` today

| File | Role |
|------|------|
| `gen_qbittorrent_test.cs` | One-shot torrent + payload generator. Deterministic seed so hashes stay stable across runs. |
| `qbittorrent_interop.cs` | Static binary-compat test (add + recheck + hash match across v1 / pure-v2 / hybrid). Shipped, passing. |
| `qbittorrent_liveswarm.cs` | Live-swarm test: qBittorrent seeds, SpawnDev.WebTorrent downloads over localhost TCP, SHA-256 verify. PASSING. |
| `js_webtorrent_liveswarm.cs` | WIP JS WebTorrent interop driver - spawns a Node.js seeder, starts a local SpawnDev.RTC.Server tracker, launches the C# client. Neither the JS nor C# client is successfully announcing to the local tracker yet; bittorrent-tracker `webrtcSupport` guard + silent reconnect-schedule backoff are the suspects. Committed as WIP scaffolding. |
| `js/` | Node.js harness: `package.json` pulls `webtorrent@^2` + `@roamhq/wrtc`. `seeder.js` wraps WebTorrent.seed with stdout protocol (READY / PEER-CONNECT / PROGRESS). Standalone works - connects to qBittorrent via DHT + public trackers. |
| `output/` | Generated artifacts (`payload.bin`, `spawndev_v1.torrent`, `spawndev_v2.torrent`, `spawndev_hybrid.torrent`). Safe to delete - regenerate via `gen_qbittorrent_test.cs`. |

## Why not run these in PlaywrightMultiTest

These scripts require a running qBittorrent instance with Web UI configured and credentials known. That's an external dependency CI can't reasonably provide. The scripts are designed for **manual / ad-hoc verification** by anyone with qBittorrent installed, paired with the hash-match regression tests inside PlaywrightMultiTest (which cover the v1 / pure-v2 / hybrid encoding correctness against known-good byte snapshots without needing a real qBittorrent).

When a need arises to automate this in CI, the right approach is either:
- Package qBittorrent + its libtorrent into a container and run it as a CI service dependency, OR
- Use libtorrent directly (no qBittorrent GUI layer) which is what the in-PlaywrightMultiTest interop fixture tests already do (`SpawnDev.WebTorrent.Demo.Shared/InteropFixtures/`).

## JS WebTorrent interop (WIP, scaffold committed)

**Goal:** Node.js `webtorrent@^2` seeder announcing to a SpawnDev.RTC.Server tracker, SpawnDev.WebTorrent C# client leeching via the same tracker + WebRTC peer-wire. Proves real interop with the reference JS WebTorrent stack (the one powering webtorrent.io), not just our own self-consistent swarm.

**Scaffold location:**
- `js/package.json` - pulls `webtorrent@^2` + `@roamhq/wrtc` (Node.js WebRTC polyfill, successor to the deprecated `wrtc`).
- `js/seeder.js` - minimal seeder with stdout protocol: `READY infohash=... magnet=...`, `PEER-CONNECT addr=...`, `PROGRESS uploaded=... peers=...`, `TRACKER-ANNOUNCE`, `WARNING ...`.
- `js_webtorrent_liveswarm.cs` - C# harness that starts an in-process Kestrel + `UseRtcSignaling("/announce")` tracker, generates a fresh torrent pointing only at that tracker, spawns the Node.js seeder, starts a C# WebTorrentClient, waits for download.

**What works:**
- Standalone JS seeder runs cleanly. `DEBUG=bittorrent-tracker:*` shows WebSocket tracker connection attempts, payload hashing, WebRTC offer generation. Cross-connects to qBittorrent via DHT + public tracker (tracker.openwebtorrent.com) when those are included.
- C# harness spins up a working local tracker (verified by `TrackerSignalingServer.Rooms.Count` probe).

**What's blocked:**
- Neither the JS seeder nor the C# leech successfully reaches the in-process local tracker. Both announce-URL'd correctly (verified in logs), both have WebRTC support enabled (wrtc top-level + `tracker.wrtc`), but `trackerSignalingServer.ConnectedPeerIds` stays empty throughout the run. Tested with `ws://127.0.0.1:<port>/announce` and confirmed Kestrel accepts the WebSocket upgrade for our own test client. No obvious rejection reason in bittorrent-tracker's debug log - it does a first-attempt socket open, then schedules reconnect at ~137 s with no visible error surfaced. Likely one of:
   1. bittorrent-tracker silently skips ws:// trackers when a `webrtcSupport` probe fails in some edge case.
   2. Our tracker's WebSocket upgrade response differs subtly from the tracker.openwebtorrent.com shape that bittorrent-tracker expects.
   3. Kestrel's in-process setup has a startup race the harness timing doesn't account for.

**Next debug steps:**
- Stand up `SpawnDev.RTC.ServerApp` in a separate process and point the harness at THAT instead of in-process. Rules out startup races.
- Log every incoming WebSocket frame on `TrackerSignalingServer.HandleWebSocketAsync` so we can see if JS's connect hits us at all.
- Try the same harness against the browser Demo (Blazor WASM) + JS seeder via Playwright - known-good path.
- Patch bittorrent-tracker with console.log of the connect error to see why it thinks the connect failed.

**What this test would close:**
- `PLAN-BEP52-External-Interop.md:23` - "JS WebTorrent is predominantly v1, so the above test almost certainly used v1-encoded torrents. libtorrent 2.0+ / qBittorrent v4.4+ remain as separate cross-client v2-peer-wire checks below." Currently only qBittorrent is automated.
- Reverse direction (seed-C# / leech-JS): symmetric follow-up once the baseline works.

## History

- **2026-04-24 morning:** Static interop shipped (`3792837` on master). All three formats PASS against qBittorrent 5.1.4 / libtorrent 2.0.11.
- **2026-04-24 evening:** Live-swarm test lands + uncovers a latent `Torrent.AddPeer` bug where already-connected peers never fired handshake. Library fix + test both green. qBittorrent seeds → SpawnDev.WebTorrent downloads → 1 MiB SHA-256 byte-identical in under 3 seconds on localhost. This closes `PLAN-BEP52-External-Interop.md` Step 4 ("live-swarm bi-directional active seeding") from the seed-qBit / leech-C# direction. The reverse direction (seed-C# / leech-qBit) is a symmetric follow-up - C# peer needs a TCP listener so qBittorrent can connect in.
- **2026-04-24 late evening:** JS WebTorrent Node.js harness scaffolded but not yet connecting to the in-process tracker. Committed as WIP with debug notes above.
