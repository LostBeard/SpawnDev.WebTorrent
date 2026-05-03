# Changelog

## 3.2.3-rc.2 (2026-05-03)

### Phantom-alive wire detection (synthesis-aware)

New `SimplePeer.IsTransportDead` virtual property (default `false`), overridden in `RtcPeer` to return `true` when the underlying transport is no longer reachable. `Wire` exposes a `SimplePeer` back-reference set in `Peer.OnConnected` so consumers walking `torrent.Wires` (notably `SpawnDev.ILGPU.P2P.P2PWebRtcBridge`) can detect transport death without scanning a separate collection.

`RtcPeer.IsTransportDead` consults a new `_lastObservedPcState` field FIRST (captured inside the `_pc.OnConnectionStateChange` event handler, which fires for both real native transitions AND synthesised `"failed"` from `SpawnDev.RTC.Browser.BrowserRTCPeerConnection`'s 15s iceDisconnected debounce poller). Falls back to the native `_pc.ConnectionState` query only if the field hasn't been populated yet. The native query lies on Chromium-under-Playwright: when the poller invokes `OnConnectionStateChange("failed")` synthetically, the underlying JS native `connectionState` stays stuck at `"connected"` indefinitely.

`_dcEverOpen` is set inside `WireDataChannel`'s `channel.OnOpen` (and the defensive "already open at subscribe time" branch), enabling `IsTransportDead` to distinguish "channel never opened (handshake)" from "channel was open and closed (terminal)" — when the data channel transitioned away from `"open"` after once being open, the transport is dead.

### What this closes

The 4.9.2-rc.34 known-issue from the SpawnDev.ILGPU.P2P 2026-04-29 EOD: `P2PSwarm.TwoTab_PeerDiscovery` failed at the 90s timeout because the bridge filter `wireSet.RemoveWhere(w => w.Destroyed)` couldn't distinguish phantom-alive wires (whose underlying transport was gone but whose `Destroyed` flag had not yet been set, because the close-event chain was still propagating) from real live wires. Result: peer never unregisters, `coord.peerCount` stuck at 1, test times out.

With the new accessor, the bridge can walk `torrent.Wires` and skip `wire.SimplePeer?.IsTransportDead == true` alongside the existing `Destroyed` skip. Verified: `TwoTab_PeerDiscovery` PASS in 1m 37s standalone (was failing in rc.34); `LargeBuffer_100MB_DispatchedOverRealWebRtc_BitExact` PASS in 3m 37s standalone (no regression vs rc.34).

### Companion bumps

- `SpawnDev.WebTorrent.Server 3.2.3-rc.2`: version-sync bump only.
- `SpawnDev.WebTorrent.Server.HuggingFace 3.2.3-rc.2`: version-sync bump only.

## 3.2.2 (2026-04-28)

Stable rollup of `3.2.2-rc.1`, `3.2.2-rc.2`, `3.2.2-rc.3`, and the polling-based SCTP backpressure fix shipped in commit `7fd70c5`. Five SCTP / wire correctness improvements over `3.2.1`. **No breaking changes.** Verified via SpawnDev.ILGPU.P2P real-WebRTC integration tests (1MB / 10MB / 100MB / multi-peer scenarios).

Pulls in **SpawnDev.RTC 1.1.8 stable** which is the OTHER half of the SCTP backpressure story (Desktop `OnBufferedAmountLow` now actually fires; was silent in 1.1.7). Without RTC 1.1.8 the backpressure wait pattern below has nothing to wait on.

### SCTP backpressure shared-TCS multi-awaiter fix (rc.2 origin)

`_bufferedAmountLowTcs` is now a SHARED `TaskCompletionSource` that every concurrent `Send` caller references at the time of their await. `OnBufferedAmountLow` completes the shared TCS to release ALL awaiters at once, then atomically installs a fresh TCS for the next round. Replaces the rc.1 `Interlocked.Exchange` pattern that orphaned all but the latest concurrent awaiter and timed out at 30s in P2P's 8-chunk pipeline.

### `RtcPeer.Send` polling-based SCTP backpressure wait (commit `7fd70c5`)

`OnBufferedAmountLow` is best-effort even with the rc.2 multi-awaiter fix. `DesktopRTCDataChannel`'s 20ms-tick poller fires only on a strict above-then-below transition observed between consecutive ticks; rapid SCTP drains miss the transition entirely and the event never fires for the next wait cycle.

`Send` now races the event TCS against a 50ms `Task.Delay`:

```csharp
await Task.WhenAny(tcs.Task, Task.Delay(50)).ConfigureAwait(false);
```

The event is still honored (resolves promptly when it fires); the 50ms poll guarantees forward progress when the event is missed. The loop's top-of-iteration `BufferedAmount` re-check then either breaks out (drained) or installs a new TCS for the next round. The 120-second wall-clock ceiling now covers genuinely-stalled SCTP rather than relying on an event we can't trust.

Diagnosed via `[RtcPeer][BACKPRESSURE-DIAG]` against `LargeBuffer_100MB_DispatchedOverRealWebRtc_BitExact`: `BufferedAmount=0` at the prior 30s/120s timeout, `ReadyState=open` - buffer drained, no event fired. After the fix: 100MB PASSES in ~5 min (was failing reliably with `TimeoutException` at 1m37s-7m7s across 3 prior runs).

### Duplicate-handshake hardening (rc.1 origin)

Phantom destroyed-wire entries no longer inflate `isLastWireForCanonical` to `false` indefinitely. The bridge filter `wireSet.RemoveWhere(w => w.Destroyed)` runs before counting in `wire.OnClose`. Closes the SpawnDev.ILGPU.P2P `P2PSwarm.TwoTab_PeerDiscovery` regression where `coord.peerCount` stayed at 1 for the full 90s budget after the worker tab closed.

### SpawnDev.RTC 1.1.8 stable dep bump

Replaces the prior 1.1.8-rc.4 transitive. RTC 1.1.8 has three additive fixes:
- Desktop `OnBufferedAmountLow` now actually fires (was silent in 1.1.7)
- `BrowserRTCPeerConnection` connection-state polling fallback (Chromium-under-Playwright)
- Opt-in `BrowserRTCPeerConnection.DiagnosticsEnabled` flag

## 3.2.2-rc.7 (2026-04-29) (superseded by 3.2.2 stable)

### Polling-based SCTP backpressure wait closes LargeBuffer_100MB regression

`RtcPeer.Send` no longer relies solely on the `OnBufferedAmountLow` event to wake from a backpressure wait. The event is best-effort on desktop - `DesktopRTCDataChannel`'s poller fires only on a strict above-then-below transition observed between 20ms polls. If `BufferedAmount` overshoots threshold and drains back BETWEEN poll ticks (rapid SCTP drain on 100MB+ transfers), the poller's `wasAboveThreshold` flag stays false and the event NEVER fires for the next wait cycle.

The previous 30s/120s `WaitAsync` would then fire a TimeoutException despite `BufferedAmount=0` and `ReadyState=open`. The exception cascaded into `Peer.SendRaw` -> `peer.Destroy(null)` -> wire close cascade -> dispatcher `HandlePeerLost` -> "P2P dispatch failed, no peers for retry: Peer X disconnected".

Diagnosed 2026-04-29 against SpawnDev.ILGPU.P2P's `LargeBuffer_100MB_DispatchedOverRealWebRtc_BitExact` via `[RtcPeer][BACKPRESSURE-DIAG]` instrumentation:

```
Wait timeout iter=1 BufferedAmount=0 MaxBuffered=65536 elapsed=120.0s
ReadyState=open dataLen=261663 bufferDelta=1
```

Buffer fully drained, channel healthy, event never fired.

**Fix:** `Send` races the event TCS against a 50ms `Task.Delay`:

```csharp
await Task.WhenAny(tcs.Task, Task.Delay(50)).ConfigureAwait(false);
```

The event is still honored (resolves promptly when it fires); the 50ms poll guarantees forward progress when the event is missed. The loop's top-of-iteration `BufferedAmount` re-check then either breaks out (drained) or installs a new TCS for the next round. The 120-second wall-clock ceiling now covers genuinely-stalled SCTP rather than relying on an event we can't trust. `BACKPRESSURE-STALL` diagnostic fires only on the rare 120s stall (real wire failure).

**Result:** `LargeBuffer_100MB_DispatchedOverRealWebRtc_BitExact` PASSES in ~5 min (was failing reliably at 1m37s-7m7s with `TimeoutException` across 3 prior runs).

## 3.2.2-rc.6 (2026-04-29)

SpawnDev.RTC 1.1.8-rc.4 dep bump. No code changes. Pulls in the opt-in `BrowserRTCPeerConnection.DiagnosticsEnabled` flag for debugging the polling-fallback path. Used by SpawnDev.ILGPU.Demo for PMT P2PSwarm.TwoTab_PeerDiscovery diagnostics.

## 3.2.2-rc.3 (2026-04-29)

SpawnDev.RTC dep bump to 1.1.8-rc.1. The rc.2 SCTP backpressure multi-awaiter fix was correct on its own but turned out to be insufficient on desktop because `SpawnDev.RTC` 1.1.7's `DesktopRTCDataChannel.OnBufferedAmountLow` was declared but never fired (SipSorcery has no native event hook). 1.1.8-rc.1 emulates the spec'd edge-triggered semantics via a 20ms-tick poller. Together the two fixes:

- rc.2: shared TCS pattern releases ALL concurrent `Send()` callers when OnBufferedAmountLow fires (was orphaning all but the most recent via Interlocked.Exchange).
- 1.1.8-rc.1: actually emits OnBufferedAmountLow on desktop (was silent in 1.1.7).

Without 1.1.8-rc.1 the rc.2 multi-awaiter improvement was a no-op on desktop because there was no signal to wait on; the 30-second WaitAsync timeout fired regardless of how clever the awaiter pattern was. Diagnosed via `[RtcPeer-DIAG]` instrumentation on `SpawnDev.ILGPU.P2P`'s `LargeBuffer_1MB_DispatchedOverRealWebRtc_BitExact`: the timeout fired with `BufferedAmount=0` and `ReadyState=open`, proving SCTP had drained and the only thing missing was the signal.

After both fixes: 1MB transfer passes 3/3 standalone in ~135s; 10MB passes (eventually) in 155-260s. Browser side unchanged - JS WebRTC fires OnBufferedAmountLow natively.

Cleanup: also reverts the temporary `[Peer] Destroy` stack-trace logging that was added during rc.2 diagnosis.

## 3.2.2-rc.2 (2026-04-29)

Critical follow-up to rc.1's SCTP backpressure work. The rc.1 implementation correctly added the `OnBufferedAmountLow` wait gate but used `Interlocked.Exchange` to install the awaiter's TCS into a single shared slot - which meant every concurrent `Send()` caller orphaned the previous one. Diagnosed against `SpawnDev.ILGPU.P2P`'s `LargeBuffer_1MB_DispatchedOverRealWebRtc_BitExact` reproduction: the test hung for 109s across 3 retries with "No healthy peers available for dispatch", traced via stack-trace instrumentation to `RtcPeer.Send` line 336 (`tcs.Task.WaitAsync`) firing `TimeoutException` after 30 seconds when the OnBufferedAmountLow signal never arrived for the orphaned awaiters.

### The race in rc.1

```csharp
// In Send (rc.1)
var tcs = new TaskCompletionSource<bool>(...);
System.Threading.Interlocked.Exchange(ref _bufferedAmountLowTcs, tcs);  // overwrite
await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

// In OnBufferedAmountLow (rc.1)
var tcs = System.Threading.Interlocked.Exchange(ref _bufferedAmountLowTcs, null);
tcs?.TrySetResult(true);  // only signals the LATEST awaiter
```

Send_1 installs `tcs1`. Send_2 fires concurrently (e.g. P2P's 8-chunk pipeline), installs `tcs2`, displacing `tcs1` into limbo. When `OnBufferedAmountLow` fires it grabs `tcs2` from the slot and signals it; `tcs1` is unreachable and times out 30s later. The `TimeoutException` propagates out of `Send`, the AsyncTaskMethodBuilder calls `SetException` on the consuming wire's pending task, the wire's `OnConnected` await chain catches it and calls `Peer.Destroy(err)`, which calls `Wire.Destroy()` and fires `wire.OnClose`. Consumers (e.g. `P2PWebRtcBridge.AttachToSwarm`) interpret the close as a real peer departure and unregister the peer mid-buffer-transfer. Net: every multi-MB tensor send that fanned out concurrent chunks broke the wire, which manifested upstack as "No healthy peers available for dispatch."

### The fix

`_bufferedAmountLowTcs` is now a SHARED TCS that every concurrent awaiter references at the time of their await. `OnBufferedAmountLow` completes that shared TCS to release ALL awaiters at once, then atomically installs a fresh TCS for the next round of waiters. New helper `EnsureBufferedAmountLowTcs()` lazily creates the slot the first time `Send` hits the threshold (before any drain has occurred), guarded by `Interlocked.CompareExchange`.

```csharp
// Send (rc.2)
var tcs = EnsureBufferedAmountLowTcs();
if (_dc.BufferedAmount <= MaxBufferedAmount) break;
await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

// OnBufferedAmountLow (rc.2)
var fresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var completed = System.Threading.Interlocked.Exchange(ref _bufferedAmountLowTcs, fresh);
completed?.TrySetResult(true);  // releases EVERY current awaiter
```

Verified: `LargeBuffer_1MB_DispatchedOverRealWebRtc_BitExact` passes with the surviving wire intact through the entire 1 MB transfer + dispatch + result return.

## 3.2.2-rc.1 (2026-04-28)

SCTP backpressure + duplicate-handshake hardening. Two bugs surfaced by Captain's deployed P2P compute demo on GitHub Pages, both in the wire-establishment / wire-teardown paths shared by every P2P consumer.

### 1. `RtcPeer.Send` now applies SCTP backpressure

Before: `RtcPeer.Send` called `_dc.Send(data)` synchronously with no check on `_dc.BufferedAmount`. WebRTC's `RTCDataChannel.send()` does not throw when the SCTP send buffer is saturated - it queues unbounded internally. Once the remote receive side detected the runaway buffer, it closed the channel with `sctpCauseCode=12` (User-Initiated Abort). Symptom in the demo: heavy buffer push-back during P2P compute (e.g. Mandelbrot strip auto-push, multi-MB tensor result buffers) intermittently killed the wire.

After: `Send` is now `async`. Before each `_dc.Send`, if `BufferedAmount > MaxBufferedAmount` (64 KB), `Send` awaits `OnBufferedAmountLow`. The data channel's `BufferedAmountLowThreshold` is set to 64 KB in `WireDataChannel`, so the event fires when the queue drops below the gate. A 30-second ceiling on the wait protects against deadlocks on a stalled wire. Sends after `Destroyed` or `ReadyState != "open"` throw a clear "Data channel closed during backpressure wait" error rather than silently failing.

### 2. `Torrent.OnHandshake` duplicate-detection skips when labels are not cross-side stable

Before: the duplicate-detection rule fell back to `peer.Id` (per-side local) when `SimplePeer.ChannelName` was empty. The responder's `ChannelName` starts as `""` and only fills in when `RtcPeer.OnDataChannel` fires. If `OnHandshake` fires inside that race window on one side and not the other, the two sides compare DIFFERENT label pairs (one with the real channel label, one with the per-side fallback `peer.Id`) and arrive at OPPOSITE keep/destroy decisions - bilaterally destroying both physical wires.

After: when either label is empty or fell back to the per-side `peer.Id`, the destroy is skipped and both wires live. Consumer-layer dedup (e.g. `P2PWebRtcBridge` deduping by remote BitTorrent peer id) collapses the duplication invisibly to the consumer; one of the wires closes naturally as `Wire.Destroy` / `Peer.Destroy` fire elsewhere.

## 3.2.0 (2026-04-25)

First minor cut since 3.1.0. Bundles five additive features on top of 3.1.7's wire-level seeder correctness fix. Zero behavior change for consumers who don't opt in to any of the new surfaces; existing 3.1.x code keeps running unchanged.

### 1. `TcpListenerService` first-class API + tracker advertising

The 3.1.8 internal cut wired `TcpListenerService` into `WebTorrentClient` as a first-class API with `WebTorrentClientOptions.TcpListenPort` / `TcpListenAddress` and `EnsureTcpListenerAsync(port, address)`. 3.2.0 keeps that surface and adds tracker advertising on top.

**New: `WebTorrentClientOptions.AdvertiseTcpListenerToTrackers`** (default `false`, opt-in). When enabled AND a `TcpListener` is bound, every HTTP / UDP tracker announce includes the listener's actual port in the BEP 3 `port=` field instead of the legacy hardcoded `0` / `6881`. Trackers put us in their compact peer list and mainline peers find us automatically.

```csharp
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    TcpListenPort = 51413,
    AdvertiseTcpListenerToTrackers = true,   // mainline peers can find us via tracker
});
```

WebSocket trackers (WebRTC signaling) are unaffected - their peer-pairing model is SDP-based, not IP+port-based. Inspect the runtime decision via `WebTorrentClient.AdvertisedTcpPort` (returns 0 when not advertising; the actual listener port when on).

**Implementation:** new `Port` field on `AnnounceOptions`; `HttpTracker.AnnounceAsync` reads `opts.Port` (replaces hardcoded `&port=0`); `Discovery.AnnounceAsync` propagates the port to UDP / WebSocket trackers; `Discovery._lastAdvertisedPort` keeps the value consistent across periodic re-announces (otherwise the first announce would advertise our port and every periodic follow-up would silently revert).

Full doc: [`Docs/tcp-listener.md`](SpawnDev.WebTorrent/Docs/tcp-listener.md). Locked by `Desktop_AdvertiseTcpListenerToTrackers_PutsListenerPortInAnnounce` (stub HttpListener captures the announce URL and asserts the port field) and `Desktop_AdvertiseTcpListenerToTrackers_DefaultIsOff` (back-compat default).

### 2. Pluggable piece-hash engine — GPU acceleration ready

New `IPieceHashEngine` interface with `Sha1`, `Sha256`, and `BatchSha256`. Default = `SystemCryptoPieceHashEngine` (System.Security.Cryptography, byte-identical to 3.1.x). Slot-in via `WebTorrentClientOptions.PieceHashEngine` and / or `WebTorrentClient.PieceHashEngine` property.

**Why pluggable:** verifying every piece of a 100 GB torrent is 25,000+ independent SHA-256 calls. Batching them through ILGPU on a desktop GPU (CUDA / OpenCL / WebGPU on browser) can be ~10-30× faster than sequential CPU. `BatchSha256(IReadOnlyList<ReadOnlyMemory<byte>>)` is the API that future GPU implementations will dispatch as one kernel batch. SpawnDev.WebTorrent intentionally does NOT take a dependency on SpawnDev.ILGPU - the GPU engine ships as a separate package (`SpawnDev.WebTorrent.GpuHash`, planned).

The v1 / Phase-1 piece-verification path (`Torrent.VerifyPieceHash` flat SHA-256 / SHA-1 case) routes through the engine. The v2 Merkle path still uses `MerkleHasher` directly - integration follows in a future cut.

Locked by `Desktop_PieceHashEngine_RoutesThroughCustomEngine` (counting-engine wrapper proves the slot-in works) and `Desktop_PieceHashEngine_DefaultsToSystemCrypto`.

### 3. Bandwidth policy

New `BandwidthPolicy` enum: `Unlimited` (default, -1 bytes/sec), `Conservative` (256 KiB/sec), `Metered` (64 KiB/sec), `SeedingDisabled` (0, paused), `Custom` (paired with explicit `UploadLimit`). Set via `WebTorrentClientOptions.BandwidthPolicy`; flip at runtime via `WebTorrentClient.ApplyBandwidthPolicy(policy)`.

```csharp
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    BandwidthPolicy = BandwidthPolicy.Metered,   // 64 KiB/sec ceiling
});
// ...later, on a wired connection again:
client.ApplyBandwidthPolicy(BandwidthPolicy.Unlimited);
```

Explicit `UploadLimit >= 0` always wins over the policy - the policy is the convenience layer for callers who want to say "be reasonable on a metered connection" without picking a number. `BandwidthPolicy.AutoDetect()` exists today as a hook (returns `Unlimited` until platform-specific signals are wired up - WinRT `IsConnectionCostMetered`, browser `navigator.connection.saveData`, NetworkInterface heuristics).

Locked by `Desktop_BandwidthPolicy_AppliesToUploadRateLimiter`, `_ExplicitUploadLimitWins`, and `_ApplyAtRuntimeSwitchesRate`.

### 4. SipSorcery fork: `MediaStreamTrack.StreamStatus` public setter

The bundled SipSorcery fork's `MediaStreamTrack.StreamStatus` setter is widened from `internal` to `public`. Consumers can now flip direction post-construction (e.g. `RecvOnly` → `SendRecv` after a remote DTLS handshake completes) without reflection or track recreation. Strict superset of the upstream surface - existing `internal` callers continue to work, public callers see what was already there.

Logged in [`UPSTREAM_BACKLOG.md`](SpawnDev.RTC/Src/sipsorcery/UPSTREAM_BACKLOG.md) for eventual upstream PR. Locked by `MediaStreamTrack_StreamStatus_PublicSetterWorks` in `SpawnDev.RTC.DemoConsole.UnitTests.DesktopForkApiTests` (the test simply uses the setter from outside the SIPSorcery assembly - if visibility ever regresses to `internal`, the test stops compiling, which is the signal we want).

### Test summary

8 new PlaywrightMultiTest tests across the five surfaces. All shipped tests + interop matrix (qBittorrent live-swarm both directions, JS WebTorrent live-swarm via local SpawnDev.RTC tracker) green.

## 3.1.8 (2026-04-25)

### `TcpListenerService` wired into `WebTorrentClient` as a first-class API

Purely additive on top of 3.1.7 - no behavior change for existing consumers, no breaking changes.

**New API surface:**

- `WebTorrentClient.TcpListener` property (`TcpListenerService?`).
- `WebTorrentClient.EnsureTcpListenerAsync(port, address)` method - idempotent; mirrors the existing `EnsureDhtAsync` precedent.
- `WebTorrentClientOptions.TcpListenPort` (`int?`) - `null` = no listener (default, back-compat); `0` = kernel-assigned ephemeral port (read `client.TcpListener.LocalEndPoint.Port` back); `> 0` = bind a specific port.
- `WebTorrentClientOptions.TcpListenAddress` (`IPAddress?`) - defaults to `IPAddress.Any` so external peers can reach the listener; pass `IPAddress.Loopback` for localhost-only test harnesses.
- Constructor fires `EnsureTcpListenerAsync` fire-and-forget when `TcpListenPort` is set.
- `client.DisposeAsync` releases the listening socket so back-to-back tests don't collide on the same port.

```csharp
// One-liner for a seeder that accepts inbound mainline-client connections:
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    TcpListenPort = 0,                   // ephemeral
    TcpListenAddress = IPAddress.Any,    // external-reachable
});
await client.EnsureTcpListenerAsync(0, IPAddress.Any);
int port = client.TcpListener!.LocalEndPoint.Port;
```

Mainline-client interop (qBittorrent, libtorrent, Transmission) unchanged - dial in by IP+port and the listener routes by info_hash to the matching torrent.

**New test:** `Desktop_TcpListenerOption_AcceptsInboundLeech` in PlaywrightMultiTest. Two clients on loopback, A seeds via the option, B leeches via direct `TcpPeer.ConnectAsync` to A's kernel-assigned port. Full 64 KiB SHA-256 byte-identical round-trip in ~10s with no peer-discovery sources enabled (deterministic - no public swarm dependency).

**Reverse-direction interop test** (`interop_test/qbittorrent_reverse_liveswarm.cs`) simplified to use the option - drops the manual `TcpListenerService` construction + dispose.

PlaywrightMultiTest full sweep: **951 pass / 0 fail / 16 skip** (+1 from 3.1.7 baseline for the new test). All three interop tests still PASS.

## 3.1.7 (2026-04-24)

### Two seeder peer-wire correctness fixes + new `TcpListenerService`

**1. `Wire._message` byte-stream corruption fix.**

The old code framed a length-prefixed wire message followed by payload bytes as TWO separate writes:

```csharp
// BROKEN
await _push(header);
await _push(data);
```

Two `_push` acquisitions of the underlying transport's send lock let two concurrent `_message` calls (e.g. four pipelined `SendPiece` responses to a leecher's request messages) interleave bytes on the wire: `header_A | header_B | data_A | data_B`. Mainline clients (qBittorrent, libtorrent) then failed SHA-1 verification on the scrambled blocks and dropped the connection after one good piece.

Fixed by building the full frame (header + payload) into a single buffer and issuing one `_push` call. Plus a `SemaphoreSlim(1, 1)` around `_push` itself for any future paths that send multiple buffers serially.

This is wire-level and benefits every transport - TCP AND WebRTC. Any consumer seeding to a pipelining leecher (the normal case in BitTorrent) avoids byte-stream corruption.

**Why latent for so long:** every prior live-swarm test had the SpawnDev.WebTorrent client as the LEECH, not the seed. Leechers send tiny `request` / `interested` / `cancel` messages, never concurrent payloads. The bug only surfaces when WE are seeding to a pipelining downloader.

**2. `TcpPeer.AttachAsync` drop-on-floor race.**

When `TcpListenerService` (new, see below) accepted an inbound connection with the BT handshake already kernel-buffered, the old single-method attach started the read loop synchronously inline. `NetworkStream.ReadAsync` resolved immediately with the buffered bytes, `EmitData` fired before the caller had wired up `OnData -> Wire.DataReceived`, and the handshake was lost.

Fixed by splitting:
- `AttachAsync` - configure the socket, fire `EmitConnect`, return.
- `StartReadLoop` - actually start reading.

`TcpListenerService` calls `AddPeer` between the two so the wire is constructed and `OnData` is wired before any inbound read fires.

**3. New `TcpListenerService`.**

Accepts inbound BitTorrent peer-wire connections, peeks the 68-byte BT handshake via `MSG_PEEK` (non-destructive kernel-buffer read), routes by info_hash to the matching torrent in the client, hands the still-unconsumed socket to a responder-mode `TcpPeer`. Closes the last open audit item in `PLAN-BEP52-External-Interop.md` Step 4 (reverse direction: seed-C# / leech-qBittorrent).

New `interop_test/qbittorrent_reverse_liveswarm.cs` drives the full live-swarm: SpawnDev.WebTorrent C# seeds, qBittorrent leeches via WebUI `addPeers` API pointing at our listener, 1 MiB hybrid torrent SHA-256 byte-identical end-to-end.

PlaywrightMultiTest full sweep: **950 pass / 0 fail / 16 skip** in 8m 7s. Zero regressions vs 3.1.6.

## 3.1.6 (2026-04-24)

### JS WebTorrent live-swarm interop PASSING

New `interop_test/js_webtorrent_liveswarm.cs` drives a real Node.js `webtorrent@^2` seeder (via `@roamhq/wrtc` - the maintained Node.js WebRTC polyfill) paired against SpawnDev.WebTorrent C# as the leech through a local `SpawnDev.RTC.ServerApp` WebSocket tracker subprocess: 1 MiB hybrid torrent, real WebRTC offer/answer exchange, SHA-256 byte-identical end-to-end. Closes the "Pure JS-WebTorrent live interop" audit gap in `PLAN-BEP52-External-Interop.md`.

### Carries the SpawnDev.RTC 1.1.6 tracker-signaling fixes

3.1.6 pulls in SpawnDev.RTC 1.1.6, which fixes two tracker-signaling bugs:

1. **`TypeInfoResolver`** explicitly set on `BinaryJsonSerializer` (client outbound) and `TrackerSignalingServer._readOpts` (server inbound) so announce serialization works under .NET 10 file-based `dotnet run script.cs` hosts and AOT/trimmed publishes (which disable reflection-based serialization by default). The `_ = _discovery.AnnounceAsync(...)` fire-and-forget in `Torrent.StartDiscovery` was silently swallowing the resulting `InvalidOperationException` on those hosts - clients never registered with any tracker.
2. **Empty-Origin bypass** on the `AllowedOrigins` allowlist - non-browser clients (desktop C# `ClientWebSocket`, Node.js `ws`, curl) don't send Origin and shouldn't be subject to a browser-origin-abuse gate. Browser-origin enforcement is unchanged.

See SpawnDev.RTC 1.1.6 CHANGELOG for the full root-cause write-up.

### Test infrastructure

- New `LocalTrackerFixture` in PlaywrightMultiTest: spins up an in-process `SpawnDev.RTC.Server` Kestrel host on a free TCP port, used by `Desktop_TwoClients_DiscoverViaTracker` and `Desktop_SeedAndAnnounce_TrackerConnects` (previously depended on hub.spawndev.com - CI-brittle).
- Sintel magnets in network / interop tests reduced to `tracker.openwebtorrent.com` only (hub doesn't host Sintel peers).
- `RetryCount = 2` on 7 live-public-swarm tests (`Network_*`, `Interop_LiveSwarm_Sintel_DownloadsPieces`, `Bep46_Loopback_*`, `UseExtension_RegistersAndCreatesExtension`, `Torrent_OnWire_FiresOnPeerConnect`).
- `Bep46_Loopback_*` ports randomized per invocation to avoid TIME_WAIT collisions on retry / cross-run reuse.
- Old `JsInteropTest.cs` + `js-interop/` script directory removed; superseded by the new `interop_test/js_webtorrent_liveswarm.cs` harness which uses `@roamhq/wrtc` and a local tracker.
- SpawnDev.UnitTesting bumped 2.5.0 → 2.5.2 across the solution (for the new `RetryCount` attribute).

PlaywrightMultiTest full sweep: **950 pass / 0 fail / 16 skip** in 7m 43s (was 477 / 5 / 6 on 3.1.5).

## 3.1.3-rc.26 (2026-04-24)

### BEP 44/46 server-side completeness

- `DhtDiscovery.HandleQuery` now dispatches BEP 44 `get` and `put` queries via new `OnGetQuery` / `OnPutQuery` events. `DhtMutableItems` wires those events to a local `_storedItems` cache: verifies Ed25519 signatures on inbound puts (rejects forgeries), serves stored values on subsequent gets. Previously SpawnDev.WebTorrent could only be a BEP 44 client, never a server, so mutable items never propagated in small swarms.
- `DhtMutableItems.PublishAsync` now self-stores so a subscriber querying the publisher directly is served the value (required when the publisher is one of the K closest nodes to the target).
- `DhtMutableItems.SubscribeAsync` bypasses the cache short-circuit - keeps polling for newer sequences instead of returning the first cached value forever.
- BEP 5 learn-from-query: `DhtDiscovery.HandleQuery` now adds the querier's NodeId to the routing table (previously only query responses populated the table). Fixes small-swarm / bootstrap scenarios.
- `Ed25519Signer.VerifyAsync` now accepts raw 32-byte BEP 44 wire-format pubkeys and wraps them in SPKI internally. Previously verify silently failed on every incoming put because `ImportEd25519Key` expects SPKI, not raw bytes. **Real production-path bug, not a test-only issue** — any incoming signed mutable item was rejected.
- New public helpers: `DhtDiscovery.FindNodeAsync(endpoint)` + `PingAsync(endpoint)` for manual-bootstrap / LAN-discovery / unit-test scenarios. New public `BuildGetResponse(txId, nodeId, value, seq, signature, publicKey)` for custom get-query handlers.
- 2 new real-P2P loopback tests: `Bep46_Loopback_PublishSubscribe_DeliversValue` (~5s) + `Bep46_Loopback_Republish_BumpsSequence` (~14s). Two in-process `DhtDiscovery` instances on loopback UDP exercise the full BEP 44/46 round-trip; the first test is the first end-to-end proof the network path works (existing tests stubbed delivery with `NotifyMutableUpdate`).

SpawnDev.WebTorrent is now among the very few BitTorrent clients with a genuinely working BEP 46 implementation - libtorrent / qBittorrent / Transmission / Deluge / WebTorrent-JS all skip BEP 46 first-class (only RangerMauve's `mutable-webtorrent` JS library ships it).

## 3.1.3-rc.23 - rc.25 (2026-04-24)

### Pure-v2 feature parity across UI + streaming + HTTP browser

- **rc.23**: Pure-v2 torrents now work end-to-end through the service-worker media streaming path and the desktop `TorrentHttpServer` file browser. `ServiceWorkerStreamHandler.GetStreamUrl` was emitting `/webtorrent/{InfoHashHex}/{fileIdx}` which collapses to `/webtorrent//{fileIdx}` for pure-v2 (empty v1 InfoHash). Fixed to use `WireInfoHashHex` (first 20 bytes of v2 hash for pure-v2, v1 hash otherwise). `WebTorrentClient.HandleStreamRequest`'s lookup switched to match `WireInfoHashHex` case-insensitively.
- **rc.24**: Dep-bump `SpawnDev.RTC` rc.3 → rc.6 (transitively: PerfectNegotiator W3C glare-free renegotiation helper; BrowserRTCSctpTransport live JSRef reads; race fix). No WebTorrent source changes.
- **rc.25**: `Torrent.DisplayNameShort` narrow-cell helper (Name → first 12 chars of WireInfoHashHex → "unknown"). Blazor demo switches from an inline 3-level ternary to `@torrent.DisplayNameShort`.

## 3.1.3-rc.20 - rc.22 (2026-04-24)

### Pure-v2 in-memory dedup, OPFS persistence, and demo UI polish

- **rc.20**: Duplicate check in `WebTorrentClient.Add(magnet)` / `Add(bytes)` / `SeedFromMetadataAsync` now uses `WireInfoHashHex` instead of raw `InfoHash`. Two calls with the same pure-v2 magnet or bytes previously created two separate Torrent entries (v1 InfoHash is empty for pure-v2 so the dedup check matched everything to everything). Plus `RemoveAsync(string)` and `RemoveWithDataAsync(string)` now delegate to `Get(string)` so callers can remove by v1, full v2, or wire-truncated hash.
- **rc.21**: Pure-v2 OPFS persistence. `Torrent.PersistMetadataAsync` / `PersistStateAsync` / the chunk-store path + `WebTorrentClient.RestoreFromStorageAsync` / `RemoveAsync(Torrent)` / `RemoveWithDataAsync(Torrent)` all key on `WireInfoHashHex` now. Pure-v2 torrents survive page reloads with pieces under `webtorrent/<v2-prefix>/` and metadata at `webtorrent/_state/<v2-prefix>.torrent`. v1-only / hybrid paths unchanged - byte-compatible with existing OPFS data. New `TorrentMetadata.WireInfoHashHex` computed property.
- **rc.22**: `Torrent.DisplayName` helper (Name → WireInfoHashHex → "unknown") so UI code can consume directly without `??` chains that silently miss pure-v2 torrents (`??` only triggers on null, not empty). Blazor + WPF demos updated. Blazor details pane now renders both `Info Hash` (v1) and `V2 Info Hash` (BEP 52) rows conditional on which hashes the torrent has.

## 3.1.3-rc.13 - rc.19 (2026-04-23 / 2026-04-24)

### Pure-v2 tracker + wire + ut_metadata + phantom-wire handshake fix

- **rc.13**: Pure-v2 tracker + wire handshake support (`Torrent.WireInfoHashHex` returns first 20 bytes of v2 SHA-256 for pure-v2 torrents, libtorrent / qBittorrent / rqbit convention). Removes the `NotSupportedException` on `magnet:?xt=urn:btmh:` magnets. `WebTorrentClient.Get(hash)` matches v1 / full v2 / wire-truncated forms. `TorrentCreator.CreateFromMultipleStreamsAsync` streaming multi-file creator (bounded memory for multi-GB HF model shards).
- **rc.14**: ut_metadata BEP 9 extension v2 variant - advertises `metadata_version: 2` in the BEP 10 extended handshake, SHA-256 verifies against the full v2 hash on receipt. Hybrid + v1-only stay on the v1 path unchanged.
- **rc.15**: Diagnostic build for Geordi's rc.12 two-popup mystery. `RtcPeer.OnDataChannel` responder path logs the ChannelName after assignment; `Torrent.OnHandshake` duplicate branch dumps full Wires state (all PeerIds + matching `_peers` + ChannelNames) before the tiebreaker decision.
- **rc.16**: `TorrentCreator.CreateFromMultipleStreamsAsync` now supports `Hybrid=true`. Single-pass streaming feeds each read to both the v2 IncrementalMerkleHasher AND the v1 SHA-1 piece buffer. End-of-file emits a zero-padded v1 piece for non-last files (matching pad-file-filled virtual stream) and a genuine partial piece for the last. Byte-identical to the in-memory BuildHybridMultiFile path.
- **rc.17**: Multi-tracker failover resilience. `Discovery.AnnounceAsync` isolates per-tracker failures - one unreachable WSS host / HTTP 5xx / UDP timeout no longer cancels the aggregate `Task.WhenAll`. Errors surface via `OnWarning`. Closes Gap 6 of Geordi's P2P audit on the WebTorrent side.
- **rc.18**: `WebTorrentClient.Add` wires the ut_metadata factory into v2 mode automatically when the magnet is pure-v2. New `TorrentParser.ParseInfoDictV2(bytes, expectedV2InfoHash)` helper.
- **rc.19**: Phantom-wire filter on the duplicate-peer tiebreaker. Geordi's rc.15 DUP-DIAG output identified orphan Wires entries from destroy-race as the two-popup peerCount=0 root cause. rc.19 filter requires `!w.Destroyed` AND a live backing peer in `_peers`. Closes the multi-iteration (rc.10 → rc.12 → rc.15 → rc.19) paired-debug effort with Geordi; verified GREEN by Geordi in 9s end-to-end via `WasmP2PBrowserTests.ComputeSwarm_Benchmark_RoundTrips_BetweenTwoPopups`.

## 3.1.3-rc.3 - rc.12 (2026-04-23)

- **rc.12**: Duplicate-peer tiebreaker on WebRTC channel Label (cross-side-stable). Replaces the rc.10-11 newcomer/existing axis which was per-side timing-dependent.
- **rc.11**: `RtcPeer` deferred `EmitConnect` when the data channel is already open at subscribe time (responder race).
- **rc.10**: First iteration of the duplicate-peer handshake tiebreaker (superseded by rc.12's correct axis).
- **rc.4**: `RtcPeer.PeerConnection` public getter - unblocked Geordi's MaxBurst consumer path for 10 MB WebRTC dispatch.
- **rc.3**: Dep-bump `SpawnDev.RTC` 1.1.3-rc.1 → 1.1.3-rc.2 (SCTP MaxBurst tunables).

## 3.1.3-rc.2 (2026-04-23)

### Pure-v2 multi-file download support

- **TorrentParser** now walks the whole v2 file tree and flattens each file's piece-layer hashes into `PieceHashes` in file-tree order. Previously only `FileRoots[0]`'s piece layer was emitted, leaving `_hashes[globalIndex]` wrong (or out of range) for any piece belonging to a file past the first. A pure-v2 multi-file torrent created by libtorrent or any external v2-first creator can now verify past file 0.
- **Per-file offsets** are now in the PADDED virtual stream — each file starts on a piece boundary with implicit zero-padding from the previous file's tail, per BEP 52 §"File tree". `TorrentFileInfo.Offset` reflects the virtual layout so the global-piece-index addressing used by BEP 3 wire request/piece messages is well-defined.
- **Per-piece length array** (`Torrent._pieceLengths[]`) replaces the old "every piece is `PieceLength` except the last" assumption. For pure-v2 multi-file, each file's last piece is `file.Length % PieceLength` bytes long; pieces straddling a file-end boundary are sized correctly so `Piece.Flush()` returns the right length and `VerifyPieceHash` hands an appropriately-short buffer to `MerkleHasher.ComputePieceLayer` (which handles leaf-level zero-padding internally).
- **TorrentCreator** (`BuildV2MultiFile`) now sorts input files by path into BEP 52 file-tree walk order before building per-file structures. Flat `PieceHashes` sequence matches what a parse round-trip produces.
- New NUnit test `VerifyPieceHashTests.V2_PureMultiFile_AllPiecesVerify_PastFile0`: creates a 3-file pure-v2 torrent (with partial last pieces on files 2 + 3), parses round-trip, walks every file's pieces and asserts `VerifyPieceHash(globalIdx, piece)` succeeds for all of them. Passes in 22ms. Mirror browser test `Bep52_PureV2MultiFile_AllPiecesVerify_PastFile0` added to the cross-platform suite.
- Existing test `TorrentCreatorV2MultiFileTests.MultiFile_V2_FlatFiles_ProducesFileTreeWithMultipleLeaves` updated: the second file's offset now asserts against the PADDED value (16384 instead of 500) since BEP 52 requires that layout for multi-file v2.
- Full NUnit regression: **256/0/0 in 5s** (from 255, +1 new test, 0 regressions).

### Wire._message + Rechoke timer hardening (concurrent work by Geordi's session)

- `Wire._message` now issues one `_push` per logical Wire message instead of two (header + data). Each BEP 10 extension message previously became two separate SCTP user messages on the WebRTC data channel; SCTP paced each independently, which stacked on top of SCTP `MAX_BURST` / `BURST_PERIOD` rate limiting and bottlenecked SpawnDev.ILGPU.P2P multi-MB tensor transfers at ~100 KB/s end-to-end despite the underlying pipe sustaining 5.4 MB/s on single-send payloads (see 3.1.3-rc.1 SctpDataSender fix).
- Hardens `Torrent.DisposeAsync` against late-firing Rechoke timer callbacks by using `Timer.DisposeAsync` to drain in-flight work, plus a defensive null filter + try/catch inside the callback.

## 3.1.3-rc.1 (2026-04-23)

### Dep-bump: SCTP sender throughput fix (via SpawnDev.RTC 1.1.3-rc.1)

- Dep-bump to `SpawnDev.RTC 1.1.3-rc.1` which transitively picks up `SpawnDev.SIPSorcery 10.0.5-rc.1` and its `SctpDataSender` lost-wakeup fix. **60x on the zero-RTT synthetic benchmark** (89.8 KB/s → 5.4 MB/s). **Real-world end-to-end throughput stays bounded by `MAX_BURST × MTU / RTT`** (~186 KB/s on loopback) until `MAX_BURST` is tunable — Geordi re-measured ~0.15–0.19 MB/s regardless of buffer size through a real DesktopRTCPeerConnection. The fix is correct; the headline 60x number only manifests when SACK RTT is effectively zero. See `SpawnDev.RTC/Docs/sctp-tuning.md` for the full analysis.
- No SpawnDev.WebTorrent source changes. Pure dep refresh.
- Full NUnit regression: **255/0/0 in 5s** (same as 3.1.2 stable since the WebTorrent library itself didn't change).
- Intended for SpawnDev.ILGPU.P2P to consume. Unblocks multi-MB tensor transfers over WebRTC data channels.

## 3.1.2 (2026-04-22 stable)

### BEP 52 v2 complete: peer-wire extension + Torrent integration + browser coverage

- **BEP 52 (BitTorrent v2) is feature-complete.** Merkle-tree piece verification, 16 KiB leaves with per-level pad-hash propagation, per-file Merkle roots, piece layers, SHA-256 info hash, `urn:btmh:` magnet URIs, hybrid v1+v2 info dicts, and the full peer-wire extension (messages 21 `hash_request` / 22 `hashes` / 23 `hash_reject`). See `Docs/bep52.md` for the walkthrough.
- Peer-wire state machine: `V2HashRequestCoordinator` allocated per v2 torrent, correlates outbound `hash_request` to inbound `hashes` on any connected wire (per-torrent, not per-wire). Handles timeout, cancellation, duplicate-key rejection, cryptographic verification, hash_reject as `HashRejectedException`.
- Seed path: `Torrent.OnV2HashRequest` uses `MerkleProofBuilder` to emit `hashes` payload from our `PieceLayers` dict, with self-check that the emitted proof re-climbs to the advertised pieces_root before transmission.
- Client path: `Torrent.RequestV2HashesAsync(req, ct, wire?)` routes through the coordinator + a picked peer Wire's `SendHashRequest`.
- Critical correctness: `Torrent.VerifyPieceHash` now branches on `MetaVersion`. v2 torrents verify against the Merkle piece-layer root (not a flat SHA-256 of the piece bytes) — this was a latent bug where large-piece-size v2 torrents would always mismatch. Caught and fixed before any user impact.
- Streaming hybrid single-file creation (`CreateHybridSingleFileFromStreamAsync`) for multi-GiB torrents in bounded memory. Multi-file hybrid inserts spec-correct pad files (`attr="p"`, `path=[".pad","N"]`).
- HuggingFaceProxy cutover to `Hybrid = true` by default. Every HF model torrent now carries both v1 SHA-1 and v2 SHA-256 infohashes.
- Cross-platform browser coverage via `WebTorrentTestBase.Bep52V2Tests.cs` — 16 tests × 2 projects through PlaywrightMultiTest. Peer-wire mirror added in the final step for 10 more browser-covered tests.
- **Test totals:** NUnit desktop 255/0/0 in 5s (from 68 pre-Phase-2 baseline + 187 new BEP 52 tests). Zero regressions on the v1 path anywhere.
- Full BEP coverage matrix: **19 BEPs** (added BEP 52). See `Docs/bep-support.md`.

### Contributing fork fixes (shipped in this version's transitive deps)

- SIPSorcery fork: `SortMediaCapability` priority-track inverted ternary fixed (upstream PR [sipsorcery-org/sipsorcery#1558](https://github.com/sipsorcery-org/sipsorcery/pull/1558)). Two peers with identical multi-codec audio lists now agree on a single negotiated format, instead of one side seeing PCMU and the other seeing Opus.

## 3.1.1 (2026-04-22)

### Packaging fix for CI / GitHub Pages consumers

- Dep-bump to `SpawnDev.RTC 1.1.1` (was `1.1.0`, which declared a dep on the un-published `SIPSorcery 10.0.4-pre` fork ID and failed to restore in external builds).
- `SpawnDev.RTC` is now a `PackageReference` (previously `ProjectReference`) so standalone WebTorrent checkouts build without the sibling RTC repo.
- `SpawnDev.WebTorrent.Server` reference to `SpawnDev.RTC.Server` similarly swapped from `ProjectReference` to `PackageReference`.
- No code changes from 3.1.0.

## 3.1.0 (2026-04-22 stable)

### Renamed fork consumption + tracker-signaling flow

- First stable cut after the WebTorrent 3.x restructure where the tracker moved to `SpawnDev.RTC.Server`. WebTorrent.Server is now web-seed-only.
- Captain manually verified bidirectional JS WebTorrent interop through hub.spawndev.com: JS seeder → JS downloader, AND JS seeder → SpawnDev.WebTorrent C# downloader. Both complete.
- Full PlaywrightMultiTest regression: 408/0/13 at this cut.

### Earlier 3.0.x development

- See git history for pre-stable 3.0.0-rc.* and 3.0.1-rc.* milestones (BEP 52 Phase 1 foundation, streaming piece flush, service worker range streaming, OPFS persistence, etc.).
