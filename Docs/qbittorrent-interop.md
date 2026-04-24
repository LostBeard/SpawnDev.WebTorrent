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

### Live-swarm interop (`qbittorrent_liveswarm.cs`) - WIP

Intended to prove that bytes actually flow between qBittorrent (seeder) and a SpawnDev.WebTorrent C# client (downloader) over a real TCP BitTorrent peer-wire. Script in `interop_test/`; the scaffolding (Web UI auth, seed setup, C# client add, direct TcpPeer connect via qBittorrent's `listen_port`) is in place but the BitTorrent handshake isn't firing from the directly-added peer. Probably needs either:
- Torrent-side wire-up that initiates the handshake when AddPeer is called with an already-connected TcpPeer (not sure if that's the lazy-initiate code path), OR
- An HTTP/UDP tracker both clients can announce to so peer discovery goes through the normal Discovery / OnTcpPeer path which is proven.

Current fallback: static hash-match-and-recheck is good enough for release confidence.

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
| `qbittorrent_liveswarm.cs` | WIP live-swarm test (qBittorrent seeds, SpawnDev.WebTorrent downloads over localhost TCP). Infrastructure works; BitTorrent handshake needs deeper investigation. |
| `output/` | Generated artifacts (`payload.bin`, `spawndev_v1.torrent`, `spawndev_v2.torrent`, `spawndev_hybrid.torrent`). Safe to delete - regenerate via `gen_qbittorrent_test.cs`. |

## Why not run these in PlaywrightMultiTest

These scripts require a running qBittorrent instance with Web UI configured and credentials known. That's an external dependency CI can't reasonably provide. The scripts are designed for **manual / ad-hoc verification** by anyone with qBittorrent installed, paired with the hash-match regression tests inside PlaywrightMultiTest (which cover the v1 / pure-v2 / hybrid encoding correctness against known-good byte snapshots without needing a real qBittorrent).

When a need arises to automate this in CI, the right approach is either:
- Package qBittorrent + its libtorrent into a container and run it as a CI service dependency, OR
- Use libtorrent directly (no qBittorrent GUI layer) which is what the in-PlaywrightMultiTest interop fixture tests already do (`SpawnDev.WebTorrent.Demo.Shared/InteropFixtures/`).

## History

- **2026-04-24 morning:** Static interop shipped (`3792837` on master). All three formats PASS against qBittorrent 5.1.4 / libtorrent 2.0.11.
- **2026-04-24 evening:** Live-swarm scaffold written during audit follow-up. Hash-match still the production confidence signal; live-swarm remains WIP.
- **PLAN-BEP52-External-Interop.md** tracks the full cross-client matrix. Step 4 ("live-swarm bi-directional active seeding") stays open until `qbittorrent_liveswarm.cs` completes the handshake.
