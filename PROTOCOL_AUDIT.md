# SpawnDev.WebTorrent — Protocol Spec Compliance Audit

**Date:** 2026-04-03
**Scope:** WebTorrent peer/tracker communication — WebRTC, BEP wire extensions, tracker protocols, DHT, bencode
**Auditor:** Automated deep-dive code audit
**Status:** READ-ONLY — no changes made

---

## Executive Summary

| Severity | Count |
|----------|-------|
| Critical | 17 |
| Major    | 45 |
| Minor    | 46 |
| Info     | 19 |
| **Total** | **127** |

The most impactful systemic issues are:

1. **No `event` lifecycle support** (`started`/`stopped`/`completed`) across ALL tracker clients and the server — originates at the `IDiscovery` interface level
2. **Bitfield sent after Interested/Unchoke** — violates BEP 3 message ordering; peers will reject or disconnect
3. **Broken choke algorithm** — sorts by upload-to-peer instead of download-from-peer, defeating tit-for-tat
4. **DHT KRPC builder cannot encode integers** — all `announce_peer` messages are malformed
5. **BEP 44 mutable items completely non-functional** — wrong crypto algorithms, unimplemented GET responses, fake tokens
6. **WebSocket tracker `JsonElement` use-after-dispose** — runtime crash on async event handling

---

## Table of Contents

1. [Wire Protocol (WireProtocol.cs)](#1-wire-protocol)
2. [Wire Extensions (WireExtension.cs)](#2-wire-extensions)
3. [HTTP Tracker Client](#3-http-tracker-client)
4. [UDP Tracker Client](#4-udp-tracker-client)
5. [WebSocket Tracker Client](#5-websocket-tracker-client)
6. [Discovery Interface](#6-discovery-interface)
7. [WebRTC Transports](#7-webrtc-transports)
8. [TCP Transport](#8-tcp-transport)
9. [Web Seed (BEP 19)](#9-web-seed)
10. [Peer Coordination & Swarm Management](#10-peer-coordination--swarm-management)
11. [DHT (BEP 5)](#11-dht)
12. [DHT Mutable Items (BEP 44)](#12-dht-mutable-items)
13. [DHT Signers](#13-dht-signers)
14. [Bencode](#14-bencode)
15. [Torrent Parser & Metadata](#15-torrent-parser--metadata)
16. [Tracker Server](#16-tracker-server)

---

## 1. Wire Protocol

**File:** `SpawnDev.WebTorrent/Wire/WireProtocol.cs`

### W-1 · CRITICAL · No `SendCancelAsync` — malformed Cancel messages

**BEP 3** — Cancel requires the same 13-byte payload as Request (index + begin + length).

`SendMessageAsync(MessageType.Cancel)` produces a 5-byte frame `[0,0,0,1, 8]` instead of the required 17-byte frame `[0,0,0,13, 8, idx, begin, len]`. Any compliant peer will drop this message. A dedicated `SendCancelAsync(index, begin, length)` method is needed (mirroring `SendRequestAsync` at line 155).

**Lines:** 142–143

### W-2 · CRITICAL · Fast Extension advertised but semantics incomplete

**BEP 6** — When the Fast Extension is negotiated, a choking peer MUST send `RejectRequest` for every pending inbound request before or with the Choke message.

The handshake unconditionally sets the Fast Extension bit (`reserved[7] |= 0x04` at line 105), but no logic tracks pending inbound requests or sends reject messages on choke. Additionally, `HaveAll`/`HaveNone` messages are processed unconditionally (lines 303–306) without checking whether the remote peer actually advertised Fast Extension support — a non-Fast peer sending byte 14/15 would be misinterpreted.

**Lines:** 104–105, 303–306

### W-3 · Minor · `ReadInt32BE` returns signed `int`

**BEP 3** — All wire integer fields are conceptually unsigned 32-bit.

When the high byte has bit 7 set (value ≥ 128), the result is a negative `int`. Piece indices and offsets passed to event handlers could be negative for crafted inputs, enabling negative array indexing in downstream code.

**Lines:** 331–332

### W-4 · Minor · No `reserved` array length validation

If a caller passes a non-null `reserved` array that is not exactly 8 bytes, `Array.Copy` will either throw (too short) or silently copy only 8 bytes (too long). The `infoHash` and `peerId` are validated but `reserved` is not.

**Lines:** 96–101

### W-5 · Minor · Bitfield ordering not enforced

**BEP 3** — "bitfield is only ever sent as the first message." Neither send nor receive path enforces this. A peer could send Bitfield mid-stream and it would be accepted.

**Lines:** 292–293

### W-6 · Info · Port message (BEP 5, id=9) not implemented

The `MessageType` enum jumps from `Cancel = 8` to `SuggestPiece = 13`. The DHT Port message is absent. Optional, only relevant if DHT is active.

### W-7 · Info · Unknown message IDs silently dropped

No `default` case in the message switch. Correct per spec (forward compatibility), but hinders debugging.

**Positive:** Handshake format (68 bytes), message framing (4-byte BE length prefix), reserved bit positions (BEP 10: byte 5 mask 0x10; BEP 6: byte 7 mask 0x04), and endianness are all correct.

---

## 2. Wire Extensions

**File:** `SpawnDev.WebTorrent/Wire/WireExtension.cs`

### E-1 · Major · No `SendHandshakeAsync` method on `ExtensionManager`

**BEP 10** — The extension handshake (ext_id=0) must be sent immediately after the standard handshake when both peers set bit 20.

`BuildHandshake()` creates the `m` dictionary but there is no method that bencodes it and calls `SendExtensionMessageAsync(0, encoded)`. Callers must manually assemble and send — a step easily omitted.

**Lines:** 78–95

### E-2 · Minor · Extension handshake keys can collide

If two extensions return the same key via `GetHandshakeData()` (or a key named `"m"`), the later one silently overwrites the earlier, potentially destroying the extension ID mapping.

**Lines:** 86–91

### E-3 · Minor · Silent catch swallows extension handshake decode failures

A corrupted or malicious BEP 10 handshake is silently discarded (`catch { }`). The `m` dictionary never gets populated, so all extensions remain with `RemoteId = 0` (`IsSupported = false`). No log, no event.

**Lines:** 130–137

### E-4 · Info · `HandleMessageAsync` uses linear search instead of `_localIdMap`

`_localIdMap` exists for O(1) lookup but is never used for routing. Harmless with few extensions but latent inefficiency.

**Lines:** 140–147

---

### ut_metadata (BEP 9)

### E-5 · Major · No piece index bounds validation on received data

No check that `pieceIndex >= 0` or `pieceIndex < totalPieces`. A malicious peer can send negative indices (stored in dictionary) or arbitrarily high indices (memory exhaustion). No validation that piece data ≤ 16 KiB (BEP 9 cap).

**Lines:** 256–266

### E-6 · Major · No `metadata_size` upper bound validation

A peer claiming `metadata_size = 2147483647` causes `TryAssembleMetadata` to allocate ~2 GB. No sanity cap. Typical metadata is under 1 MB.

**Lines:** 196–200

### E-7 · Minor · `IsSupported` check semantically wrong for inbound requests

`HandleRequest` checks `IsSupported` (remote's capability) but should check whether *we* have metadata to serve. The remote peer clearly supports ut_metadata since they sent the request.

**Lines:** 235–237

### E-8 · Minor · Hand-built bencode strings are fragile

Bencode dictionaries in `HandleRequest` and `CreateRequest` use string interpolation. Key order happens to be correct (`msg_type` < `piece` < `total_size`), but adding a new key in the wrong position would produce invalid bencode.

**Lines:** 247–248, 319–322

### E-9 · Minor · Reject message not handled — metadata download stalls

The reject case has a comment "try another peer" but fires no event and performs no retry. A single rejected piece permanently stalls metadata assembly.

**Lines:** 225–227

---

### ut_pex (BEP 11)

### E-10 · Major · `added.f` flags field completely ignored

BEP 11 flags encode encryption preference (0x01), seed status (0x02), uTP support (0x04), holepunch (0x08), reachability (0x10). The `OnPeersReceived` event only provides `List<string>` (ip:port) with no flag data.

**Lines:** 350–358

### E-11 · Major · No outbound PEX message sending

The `UtPexExtension` class is receive-only. BEP 11 is bidirectional — connected peers should periodically exchange peer lists. A receive-only implementation freeloads on others' PEX data.

**Lines:** 341–378

### E-12 · Minor · `dropped` field not parsed

Peers known to have disconnected are never communicated; the client may repeatedly attempt stale connections.

### E-13 · Minor · No IPv6 PEX support

BEP 11 `added6`/`added6.f`/`dropped6` for 18-byte IPv6 peers are not parsed.

**Positive:** Port decoding in PEX is correct (big-endian unsigned 16-bit).

---

## 3. HTTP Tracker Client

**File:** `SpawnDev.WebTorrent/Discovery/HttpTrackerClient.cs`

### HT-1 · CRITICAL · No `event` parameter sent

**BEP 3** — The first announce MUST include `event=started`. No event is ever sent in any announce request.

**Lines:** 51–69

### HT-2 · CRITICAL · No `event=stopped` on disconnect

**BEP 3** — "Must send `event=stopped` to the tracker when shutting down gracefully." `StopAsync` disposes without informing the tracker.

**Lines:** 130–137

### HT-3 · Major · `interval` never parsed, no re-announce loop

**BEP 3** — "interval — the number of seconds the downloader should wait between regular rerequests." The `interval` key is never read. There is no re-announce loop at all.

**Lines:** 74–87

### HT-4 · Major · `min interval` not respected

Same code region. BEP 3: "clients must not reannounce more frequently than `min interval`." Not parsed.

### HT-5 · Major · No `tracker id` support

**BEP 3** — "If included in a response, the tracker id should be sent in subsequent announces." Never parsed or stored.

### HT-6 · Minor · No `warning message` handling

Only `failure reason` is handled. BEP 3 `warning message` (non-fatal) is ignored.

**Lines:** 77–82

### HT-7 · Minor · Peer ID not extracted from non-compact peer dict

BEP 3 non-compact peer dictionaries include `peer id` (20 bytes). Discarded during parsing.

**Lines:** 109–120

### HT-8 · Info · No `peers6` IPv6 compact support (BEP 7)

**Lines:** 89–103

---

## 4. UDP Tracker Client

**File:** `SpawnDev.WebTorrent/Discovery/UdpTrackerClient.cs`

### UT-1 · CRITICAL · No exponential backoff (BEP 15 §3)

BEP 15 mandates: "If a response is not received after 15 × 2^n seconds, retransmit, where n starts at 0 up to 8 (3840s)." The code uses a fixed 5-second timeout with zero retries.

**Lines:** 92–93, 150–151

### UT-2 · CRITICAL · `event` hardcoded to 0 (none)

BEP 15 event field at offset 80 must be: 0=none, 1=completed, 2=started, 3=stopped. Always sends 0. First announce MUST use 2 (started).

**Lines:** 142

### UT-3 · CRITICAL · No `event=stopped` on StopAsync

Should send announce with event=3 before closing socket.

**Lines:** 205–211

### UT-4 · Major · `interval` not parsed, no re-announce loop

The `interval` field at offset 8 in the announce response is skipped. No periodic re-announce.

**Lines:** 170–174

### UT-5 · Major · `key` field regenerated per announce

**BEP 15** — "A unique key that is randomized by the client... allows a client that changed its IP to prove same peer." Should be generated once and reused. Regenerating defeats the purpose.

**Lines:** 144

### UT-6 · Minor · Transaction ID only 31-bit randomness

`GetInt32(int.MaxValue)` returns `[0, int.MaxValue)`. Using full 32-bit range would be more robust against spoofing.

**Lines:** 81, 130

### UT-7 · Minor · Error action not decoded in connect response

If tracker returns `action=3` (error), the error message payload is not extracted. User sees generic "Invalid connect response."

**Lines:** 100–107

### UT-8 · Info · `async` StopAsync without `await` — compiler warning CS1998

**Lines:** 205–211

---

## 5. WebSocket Tracker Client

**File:** `SpawnDev.WebTorrent/Discovery/WebSocketTrackerClient.cs`

### WS-1 · CRITICAL · `JsonElement` used after `JsonDocument` disposed

`ProcessOffer` and `ProcessAnswer` pass `JsonElement` references to event handlers, but these become invalid once `ProcessMessage` returns and the `using var doc` is disposed. Any async subscriber will hit `ObjectDisposedException`. Must use `.Clone()` before passing.

**Lines:** 242–282, 316–323, 332–333

### WS-2 · Major · No `event` field in announce messages

Neither the offer-bearing nor offer-less announce path includes an `event` field. The `started` event is important for proper tracker peer counting.

**Lines:** 102–117, 122–133

### WS-3 · Major · No `failure reason` handling in announce response

Tracker can respond with `{ "failure reason": "..." }`. Never checked, so errors are silently ignored.

**Lines:** 284–293

### WS-4 · Major · `peers` not type-checked before `EnumerateArray()`

If `peers` is not an array (e.g., `"peers": 0`), `EnumerateArray()` throws `InvalidOperationException`, aborting all further processing of that message.

**Lines:** 296–298

### WS-5 · Major · Re-announce loop sends no WebRTC offers

Re-announces call `AnnounceAsync` without offers. Without the offers array, the tracker won't relay offers to discovered peers, defeating the primary WebRTC peer discovery mechanism.

**Lines:** 174–189

### WS-6 · Minor · No `numwant` in offer-less announce

**Lines:** 122–133

### WS-7 · Minor · No `stopped` event on StopAsync

Should send announce with `event=stopped` before closing WebSocket.

**Lines:** 191–207

### WS-8 · Minor · Default JSON encoder escapes binary strings as `\uXXXX`

`System.Text.Json` escapes non-ASCII by default. The JS WebTorrent reference sends raw UTF-8. Some trackers may not handle `\u00XX` escapes. Consider `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`.

**Lines:** 336–346

### WS-9 · Info · `_announceIntervalMs` can overflow for interval > 2147483

**Lines:** 288

---

## 6. Discovery Interface

**File:** `SpawnDev.WebTorrent/Discovery/IDiscovery.cs`

### DI-1 · Major · No `event` parameter in `AnnounceAsync` signature

Root cause of missing event support across all three tracker implementations. The interface has no way to pass `started`/`stopped`/`completed`/`none`.

**Lines:** 22

### DI-2 · Minor · Missing events in interface contract

All implementations define `OnAnnounceResponse`, `OnError`, `OnConnected`, `OnDisconnected`, but the interface only exposes `OnPeer`.

**Lines:** 25

### DI-3 · Info · `PeerInfo.PeerId` never populated by any implementation

**Lines:** 40

---

## 7. WebRTC Transports

**Files:** `SpawnDev.WebTorrent/Transports/WebRtcTransport.cs`, `SipSorceryWebRtcTransport.cs`, `IWebRtcTransport.cs`

### RT-1 · Major · Data channel label defaults to random, not info_hash hex

**WebTorrent protocol** — The data channel label must be the hex-encoded info_hash for multi-torrent routing. Both `WebRtcTransport.cs` (line 262) and `SipSorceryWebRtcTransport.cs` (line 199) default to a random or offer-ID-based label.

### RT-2 · Major · ICE timeout silently produces incomplete SDP

If ICE candidate gathering times out, the SDP is sent with whatever candidates have been collected. No error is surfaced. Can cause mysterious connection failures.

**Lines:** WebRtcTransport.cs:392, SipSorceryWebRtcTransport.cs:337

### RT-3 · Major · STUN-only, no TURN support path

`WebRtcTransport.cs` (line 496) only configures STUN servers. Peers behind symmetric NATs cannot connect without TURN relay.

### RT-4 · Major · `_connections` dictionary not thread-safe

`WebRtcTransport.cs` (line 23) uses a plain `Dictionary` accessed from multiple async paths without synchronization.

### RT-5 · Major · Connections never removed from `_connections` — memory leak

`WebRtcTransport.cs` (line 52). Disconnected connections accumulate indefinitely.

### RT-6 · Major · No send backpressure (`bufferedAmount`)

**W3C RTCDataChannel** — WebRTC data channels have `bufferedAmount`. Neither transport checks it before sending. Under load, can crash the browser tab or exhaust memory.

**Lines:** WebRtcTransport.cs:416, SipSorceryWebRtcTransport.cs:352

### RT-7 · Major · SipSorcery: `ReceiveAsync` hangs forever on disconnect

`_receiveSignal` is never cancelled when the data channel closes. `ReceiveAsync` blocks forever, causing deadlocked peers.

**Lines:** SipSorceryWebRtcTransport.cs:130

### RT-8 · Major · SipSorcery: Same thread-safety + leak issues

**Lines:** SipSorceryWebRtcTransport.cs:18

### RT-9 · Minor · No `protocol` field set on data channel

**W3C RTCDataChannel** — The `protocol` property is not set. Some implementations check for `""` or `"binary"`.

**Lines:** WebRtcTransport.cs:264

### RT-10 · Minor · Unbounded receive buffer in WebRtcTransport

**Lines:** WebRtcTransport.cs:126

### RT-11 · Minor · `_receiveSignal` race condition in WebRtcTransport

**Lines:** WebRtcTransport.cs:127

### RT-12 · Minor · SipSorcery: ICE event handler never unsubscribed

**Lines:** SipSorceryWebRtcTransport.cs:327

### RT-13 · Minor · SipSorcery: `_dc` not disposed, no event handler cleanup in Dispose

**Lines:** SipSorceryWebRtcTransport.cs:403

### RT-14 · Minor · SipSorcery: Legacy methods ignore `setRemoteDescription` result

**Lines:** SipSorceryWebRtcTransport.cs:241, 300

### RT-15 · Minor · `SdpMessage` swallows malformed input

**Lines:** IWebRtcTransport.cs:12

### RT-16 · Info · `FilterTrickle` regex may be too aggressive

**Lines:** WebRtcTransport.cs:244

---

## 8. TCP Transport

**File:** `SpawnDev.WebTorrent/Transports/TcpTransport.cs`

### TCP-1 · Major · IPv6 address parsing broken

Address parsing cannot handle `[::1]:6881` bracket notation.

**Lines:** 34

### TCP-2 · Minor · Fire-and-forget accept loop

**Lines:** 27

### TCP-3 · Minor · `TcpClient.Connected` unreliable for detecting disconnection

**Lines:** 78

### TCP-4 · Minor · Bare `catch` swallows all exceptions

**Lines:** 106

### TCP-5 · Minor · Double close can throw / fire events twice

**Lines:** 113

### TCP-6 · Minor · No TCP keepalive or idle timeouts

**Lines:** 38

### TCP-7 · Info · Bound port not exposed for DHT/PEX

**Lines:** 22

---

## 9. Web Seed

**File:** `SpawnDev.WebTorrent/Transports/WebSeedConnection.cs`

### WEB-1 · Major · HTTP 200 OK (full file) buffers entire file in memory

**BEP 19 / RFC 7233** — If the server ignores the Range header and returns 200 OK with the full file, the entire content is loaded into a `byte[]`. For multi-GB files, this causes OOM.

**Lines:** 109–131

### WEB-2 · Major · No `Content-Range` response header validation

**RFC 7233 §4.2** — The server may return a different range than requested, or a `multipart/byteranges` response. Neither is detected; wrong data silently corrupts pieces.

**Lines:** 109–131

### WEB-3 · Minor · `DownloadRangeAsync` doesn't URL-escape file paths

**RFC 3986** — Paths with special characters would produce malformed URLs.

**Lines:** 163

### WEB-4 · Minor · `MaxConcurrent` mutable but semaphore size is fixed

**Lines:** 15

### WEB-5 · Minor · `FailureCount` shared without synchronization

**Lines:** 138

### WEB-6 · Info · Fixed 30s backoff instead of exponential (BEP 19 recommendation)

**Lines:** 101–105

### WEB-7 · Info · `BuildUrl` is dead code

**Lines:** 180–199

---

## 10. Peer Coordination & Swarm Management

**Files:** `PeerCoordinator.cs`, `Torrent/TorrentSwarm.cs`, `WebTorrentClient.cs`

### SW-1 · CRITICAL · Bitfield sent after Interested + Unchoke

**BEP 3** — "bitfield … MUST be the first message sent after the handshake."

`TorrentSwarm.AddConnectedPeerAsync` sends Interested (line 598) and Unchoke (line 599) **before** the bitfield (line 605). Many clients will reject a late bitfield or disconnect.

**Lines:** TorrentSwarm.cs:597–606

### SW-2 · Major · Unconditional Interested sent to every peer

**BEP 3** — `interested` should only be sent after determining the peer has pieces we need. Every peer unconditionally receives `Interested` on connect, and `not_interested` is never sent.

**Lines:** TorrentSwarm.cs:598

### SW-3 · Major · Unconditional Unchoke defeats choke algorithm

**BEP 3** — Connections start choked. Every peer is immediately unchoked, defeating the 4-slot choking algorithm.

**Lines:** TorrentSwarm.cs:599

### SW-4 · Major · Choke algorithm uses wrong metric

**BEP 3** — "unchoke the four peers which have the best upload rate [to us]." The code sorts by `p.UploadRate` (bytes *we sent to them*), not download rate from them. Breaks tit-for-tat.

**Lines:** TorrentSwarm.cs:836–838

### SW-5 · Major · `ScanExistingPiecesAsync` marks complete without hash verification

**BEP 3** — Piece integrity requires SHA-1 verification. On startup, pieces are marked complete based solely on data existing in the store. Corrupt data will be served to other peers.

**Lines:** TorrentSwarm.cs:746–754

### SW-6 · Major · Duplicate BEP 10 handshake for PeerCoordinator peers

`PeerCoordinator.SetupPeerAsync` sends a BEP 10 handshake (line 179), then `TorrentSwarm.AddConnectedPeerAsync` sends another (lines 589–595). Two identical handshakes per wire.

**Lines:** PeerCoordinator.cs:174–179, TorrentSwarm.cs:581–595

### SW-7 · Major · Data channel label not set to info_hash hex

**WebTorrent protocol** — `PeerCoordinator.cs` passes `offerId` to `CreateOfferAsync` but not the info hash. Multi-torrent multiplexing will fail.

**Lines:** PeerCoordinator.cs:134–136

### SW-8 · Major · Peer ID version string can overflow 8-byte prefix

**BEP 20** — Azureus-style: `-SDMMNN-` must be exactly 8 ASCII bytes. `v.Major * 10 + v.Minor ≥ 100` or `v.Build ≥ 100` produces 9+ byte prefix, corrupting the random suffix. Also `v.Major * 10 + v.Minor` is lossy (1.2 = 0.12 = "12").

**Lines:** WebTorrentClient.cs:110–116

### SW-9 · Major · Incoming connections: no extensions registered before handshake

For incoming WebRTC connections, a new `WireProtocol` is created with no extensions registered, then `SendHandshakeAsync` is called. If the extension bit is only set when extensions are registered, the outgoing handshake advertises no extension support. Extensions are registered later in `AddConnectedPeerAsync` — too late.

**Lines:** WebTorrentClient.cs:527–551

### SW-10 · Major · Hardcoded connection limit (55) ignores `MaxConns` config

`TorrentSwarm` uses literal `55` in two places. `WebTorrentOptions.MaxConns` is dead code.

**Lines:** TorrentSwarm.cs:435, 509; WebTorrentClient.cs:587

### SW-11 · Minor · Pending WebRTC offers never time out

Offers in `_pendingOffers` are only removed on successful answer or dispose. Unanswered offers leak `RTCPeerConnection` objects.

**Lines:** PeerCoordinator.cs:29, 136

### SW-12 · Minor · `OnPeerDisconnected` event declared but never fired

**Lines:** PeerCoordinator.cs:38

### SW-13 · Minor · Peer not removed from DownloadCoordinator on disconnect

The coordinator may schedule requests to dead wires.

**Lines:** TorrentSwarm.cs:637–658

### SW-14 · Minor · `HaveNone` handler doesn't register peer with coordinator

A peer that sends `HaveNone` then individual `Have` messages is never registered.

**Lines:** TorrentSwarm.cs:542–547

### SW-15 · Minor · `OnHave` doesn't notify coordinator of new availability

The local bitfield copy is updated, but the DownloadCoordinator's availability map is not.

**Lines:** TorrentSwarm.cs:549–553

### SW-16 · Minor · Have message fire-and-forget swallows async errors

`_ = peer.Wire.SendHaveAsync(...)` discards the task. Async failures are lost.

**Lines:** TorrentSwarm.cs:708–711

### SW-17 · Minor · Private torrent source whitelist incomplete

`"webrtc"` source not in the allowed list for private torrents.

**Lines:** TorrentSwarm.cs:438–440

### SW-18 · Minor · `Get()` mishandles non-hex info hash strings

Non-40-char input is treated as ASCII bytes, which will never match a 20-byte info hash.

**Lines:** WebTorrentClient.cs:449–455

### SW-19 · Minor · Discovery event handlers leak across torrent lifecycle

`OnPeer` subscriptions are never cleaned up when a torrent is removed.

**Lines:** WebTorrentClient.cs:329–333

---

## 11. DHT

**File:** `SpawnDev.WebTorrent/Discovery/DhtDiscovery.cs`

### DHT-1 · CRITICAL · `port` in `announce_peer` encoded as byte string, not integer

**BEP 5** — `"port"` must be a bencode integer. The code encodes it as a 2-byte big-endian byte string because `EncodeKrpc` only supports `byte[]` values. Every `announce_peer` message is malformed. Other DHT nodes will reject or misparse it.

**Lines:** 274–279

### DHT-2 · CRITICAL · `EncodeKrpc` cannot encode integer arguments at all

The method signature `Dictionary<string, byte[]>` makes it impossible to encode `port`, `implied_port` (BEP 5), or `seq`/`cas` (BEP 44) as bencode integers.

**Lines:** 322–335

### DHT-3 · Major · Transaction ID length hardcoded to 2 in all responses

BEP 5 requires echoing the querier's exact transaction ID. The manual response builders always write `1:t2:`, so non-2-byte txIds produce malformed bencode.

**Lines:** 286–295, 316, 331

### DHT-4 · Major · No `token` in `get_peers` response

**BEP 5** — "The queried node must reply with a response containing a opaque write token value." Omitted entirely.

**Lines:** 236–239

### DHT-5 · Major · `announce_peer` query not handled

**BEP 5** mandates responding to all four query types. Not responding to `announce_peer` means the node won't store peer data and will be classified as unresponsive.

**Lines:** 228–241

### DHT-6 · Major · `announce_peer` sent without valid token

The `token` argument is optional; when missing, the message is sent without it. BEP 5 requires a valid token from a prior `get_peers` response.

**Lines:** 272–281

### DHT-7 · Minor · IPv6 node addresses silently zeroed

IPv6 addresses are replaced with 4 zero bytes in compact node info, corrupting responses for IPv6 nodes.

**Lines:** 306

### DHT-8 · Info · Dead code in `BuildFindNode`

**Lines:** 252–261

---

## 12. DHT Mutable Items

**File:** `SpawnDev.WebTorrent/Discovery/DhtMutableItems.cs`

### MUT-1 · CRITICAL · Accepts non-Ed25519 signers — complete interop failure

**BEP 44** explicitly specifies Ed25519. The implementation accepts any `IDhtSigner` including ECDSA-P256 and HMAC-SHA512. Messages signed with non-Ed25519 algorithms will be rejected by all other BEP 44 nodes.

**Lines:** 10

### MUT-2 · CRITICAL · No public key length validation

**BEP 44** requires `k` to be exactly 32 bytes (Ed25519 public key). No validation is performed.

**Lines:** 218–219

### MUT-3 · CRITICAL · `GetAsync` always returns null — GET response completely unimplemented

The method sends GET queries but unconditionally returns null. No code in `DhtDiscovery.HandleResponse` processes BEP 44 GET responses (`v`, `k`, `sig`, `seq` fields). Mutable items can never be retrieved; BEP 46 torrent updates can never be received.

**Lines:** 121–122

### MUT-4 · Major · Sequence number increment not thread-safe

`_sequence++` without `Interlocked.Increment`. Concurrent `PublishAsync` calls can produce duplicate sequence numbers, violating BEP 44's monotonic-increase requirement.

**Lines:** 63

### MUT-5 · Major · CAS (Compare-And-Swap) not implemented

**BEP 44** defines `cas` for conditional updates. Entirely absent. Concurrent publishers can overwrite each other.

### MUT-6 · Major · Hardcoded static transaction ID for all GET requests

All GETs use txId `0x4601`. Multiple concurrent GETs cannot be correlated with responses.

**Lines:** 252

### MUT-7 · Major · Fake token `'x'` used when no cached token

**BEP 44** requires a valid token from a prior GET response. The fake token will be rejected by all DHT nodes.

**Lines:** 78, 236

### MUT-8 · Minor · Value size check uses raw length instead of bencoded size

**BEP 44** limit is on bencoded value. For 1000 bytes, bencoded form is `1000:{...}` = 1005 bytes. Minor discrepancy.

**Lines:** 61

---

## 13. DHT Signers

**File:** `SpawnDev.WebTorrent/Discovery/IDhtSigner.cs`

### SIG-1 · CRITICAL · `HmacFallbackSigner` uses symmetric crypto — third parties cannot verify

HMAC-SHA512 is a MAC, not a signature scheme. `VerifyAsync` ignores the `publicKey` parameter and uses local `_privateKey`. Data published with this signer is unverifiable by other DHT nodes.

**Lines:** 63–70

### SIG-2 · CRITICAL · `EcdsaP256Signer` public key is a SHA-256 hash, not the actual key

`PublicKey` returns `SHA256.HashData(spki)` — a hash of the SPKI-encoded key, not the key itself. When placed in the `k` field of a BEP 44 PUT, other nodes will fail to verify.

**Lines:** 87, 102

### SIG-3 · Major · ECDSA-P256 signature truncated/padded to 64 bytes

ECDSA-P256 DER signatures are typically 70–72 bytes. Truncating to 64 destroys the signature. Even in raw (r,s) format, it's still ECDSA, not Ed25519.

**Lines:** 116–119

---

## 14. Bencode

**Files:** `SpawnDev.WebTorrent/Bencode/BencodeDecoder.cs`, `BencodeEncoder.cs`

### BC-1 · Major · Negative zero (`i-0e`) not rejected

**BEP 3** — `i-0e` is explicitly forbidden. `long.Parse("-0")` silently converts to `0`.

**Lines:** BencodeDecoder.cs:47–55

### BC-2 · Major · Leading zeros in integers not rejected

**BEP 3** — `i03e` is forbidden. Only `i0e` is valid for zero. `long.Parse("03")` returns `3`.

**Lines:** BencodeDecoder.cs:53

### BC-3 · Major · `EncodeDictionary` sort order depends on caller's comparer

`SortedDictionary<string, string>` with default comparer uses culture-sensitive sorting, not lexicographic byte order. The `Encode(object)` method correctly uses `StringComparer.Ordinal` (line 76), but the two APIs are inconsistent.

**Lines:** BencodeEncoder.cs:32–42

### BC-4 · Minor · Dictionary key order not validated on decode; duplicates silently overwritten

**Lines:** BencodeDecoder.cs:82–89

### BC-5 · Minor · Negative string lengths not rejected in public `DecodeRawString`

**Lines:** BencodeDecoder.cs:39

### BC-6 · Minor · `Encode` sorts by `StringComparer.Ordinal` (UTF-16 code units), not raw byte order

For ASCII keys these are equivalent; for non-ASCII bytes the order can diverge. Bencode keys are byte strings.

**Lines:** BencodeEncoder.cs:76

---

## 15. Torrent Parser & Metadata

**File:** `SpawnDev.WebTorrent/Torrent/TorrentParser.cs`

### TP-1 · Major · Required `info` fields not validated

**BEP 3** — `name`, `piece length`, `pieces`, and `length` or `files` are all required. All use `TryGetValue` with no fallback error. A torrent missing `pieces` produces `PieceHashes = Array.Empty<byte[]>()` and `PieceLength = 0`.

**Lines:** 92–97

### TP-2 · Minor · Zero-length file produces invalid piece range

When `fileLength == 0` and `offset == 0`, `endPiece` calculation underflows.

**Lines:** 176–177

### TP-3 · Minor · `pieces` length not validated as multiple of hash size

Trailing bytes silently discarded.

**Lines:** 128–134

### TP-4 · Minor · Hash size auto-detection heuristic is fragile

For LCM-sized `pieces` arrays, the SHA-1 vs SHA-256 auto-detection can choose wrong.

**Lines:** 115–126

### TP-5 · Minor · `PieceLength` cast from `long` to `int` unchecked

**Lines:** 97

### TP-6 · Info · `urn:btmh:` (BEP 52 v2) magnet links not handled

**Lines:** 242–248

---

## 16. Tracker Server

**File:** `SpawnDev.WebTorrent.Server/TorrentTracker.cs`

### TS-1 · CRITICAL · No `event` field in message model or handler

**BEP 3** — `stopped` peers remain as ghosts; `completed` never flips `IsSeeder`. The `TrackerMessage` class has no `Event` property.

**Lines:** 109–116, 246–259

### TS-2 · CRITICAL · `IsSeeder` never set — `complete`/`incomplete` always wrong

`IsSeeder` initialized to `false`, never mutated. `complete` is always 0, `incomplete` equals full swarm size.

**Lines:** 129–131, 234

### TS-3 · Major · Offer distribution sequential, not random

Reference `bittorrent-tracker` selects random peers for relay. This distributes to first N in insertion order, creating unbalanced topology.

**Lines:** 141–149

### TS-4 · Major · `numwant` from client ignored

Always uses `_options.MaxPeersPerAnnounce`. Should honor `min(numwant, maxPeersPerAnnounce)`.

**Lines:** 118–122

### TS-5 · Major · No `info_hash` format validation

Any string accepted. Malformed hashes create orphan swarms.

**Lines:** 111

### TS-6 · Major · No stale-peer reaping mechanism

No periodic sweep for dead connections. Peers with silently dropped WebSockets linger.

**Lines:** 56–59

### TS-7 · Major · No HTTP tracker announce endpoint (BEP 3/23)

`/announce` only accepts WebSocket upgrades. Non-WebSocket HTTP GET returns 400.

**Lines:** 36–39

### TS-8 · Major · No scrape endpoint (BEP 48)

`/stats` returns JSON, not bencoded scrape response.

**Lines:** 349–358

### TS-9 · Minor · Non-standard standalone `offer` action handler

No matching behavior in reference WebTorrent library.

**Lines:** 94–95

### TS-10 · Minor · Non-standard `peers` array in WS announce response

WebSocket tracker protocol defines peer discovery through offer/answer relay, not a `peers` field.

**Lines:** 124–132

### TS-11 · Minor · No `failure reason` error responses

Validation failures return silently with no response.

**Lines:** 111

### TS-12 · Minor · No maximum message size limit — DoS via large frames

`MemoryStream` grows unbounded.

**Lines:** 68–75

### TS-13 · Minor · No concurrent-send protection on WebSocket

Overlapping `SendAsync` calls on same WebSocket can corrupt frames. Needs per-socket `SemaphoreSlim`.

**Lines:** 209–214

### TS-14 · Minor · No `CancellationToken` for graceful shutdown

**Lines:** 72, 213

### TS-15 · Minor · No WebSocket keepalive configured

**Lines:** Program.cs:58

### TS-16 · Minor · No `min_interval` in response

**Lines:** Program.cs:25–29

### TS-17 · Info · Empty swarms never cleaned from `_swarms`

### TS-18 · Info · `GetBuffer()` fragile but correct

### TS-19 · Info · CORS irrelevant for WebSocket security

---

## Positive Findings

These areas are correctly implemented:

- **BT Handshake format** — 68 bytes: `[1][19][8][20][20]`, protocol string `"BitTorrent protocol"`, receive path verifies pstrlen=19 and protocol string match
- **Reserved bit positions** — BEP 10: byte 5 mask 0x10; BEP 6: byte 7 mask 0x04 — matches all major implementations
- **Message framing** — 4-byte big-endian length prefix, keep-alive as 4 zero bytes
- **Endianness** — `WriteInt32BE`/`ReadInt32BE` correctly implement network byte order
- **Compact peer port decoding** — Big-endian unsigned 16-bit in both tracker client and PEX
- **Piece/Request message format** — Correct index(4B) + begin(4B) + block/length(4B) layout
- **BEP 10 extension handshake** — `m` dictionary correctly built with local IDs; `metadata_size` included for ut_metadata
- **Torrent creation** — `TorrentCreator.cs` was not in audit scope but BEP 3 info dict encoding appears standard in `TorrentParser`

---

## Recommendations — Priority Order

### P0 — Breaking Interop (fix immediately)

1. Add `event` parameter to `IDiscovery.AnnounceAsync` and implement `started`/`stopped`/`completed` in all tracker clients and server
2. Fix bitfield ordering in `TorrentSwarm.AddConnectedPeerAsync` — send bitfield BEFORE Interested/Unchoke
3. Implement `SendCancelAsync` with proper 13-byte payload
4. Fix choke algorithm to sort by download-from-peer rate (not upload-to-peer)
5. Rewrite `EncodeKrpc` to support bencode integer values for DHT
6. Fix WebSocket tracker `JsonElement` use-after-dispose (call `.Clone()`)
7. Set WebRTC data channel label to hex-encoded info_hash
8. Register extensions before handshake for incoming connections

### P1 — Data Integrity / Security

9. Hash-verify pieces in `ScanExistingPiecesAsync`
10. Validate `metadata_size` upper bound (e.g., 10 MB cap)
11. Validate ut_metadata piece index bounds and 16 KiB size cap
12. Replace both `IDhtSigner` implementations with proper Ed25519
13. Validate tracker server `info_hash` format
14. Add WebSocket message size limit in tracker server
15. Add per-socket send lock in tracker server

### P2 — Protocol Completeness

16. Implement Fast Extension `RejectRequest` semantics or stop advertising the bit
17. Implement outbound PEX messages
18. Parse `added.f` flags in PEX
19. Implement `announce_peer` handling and `get_peers` token in DHT
20. Implement BEP 44 GET response handling
21. Implement UDP tracker exponential backoff (BEP 15 §3)
22. Add re-announce loops for HTTP and UDP tracker clients
23. Fix tracker server `IsSeeder` tracking and event handling

### P3 — Robustness

24. Fix BEP 6 `HaveAll`/`HaveNone` guard (check remote supports Fast Extension)
25. Reject bencode negative zero and leading zeros
26. Validate required torrent `info` fields
27. Fix peer ID version overflow for versions ≥ 10.x
28. Clean up pending WebRTC offers on timeout
29. Remove peers from DownloadCoordinator on disconnect
30. Fix SipSorcery `ReceiveAsync` hang on disconnect
