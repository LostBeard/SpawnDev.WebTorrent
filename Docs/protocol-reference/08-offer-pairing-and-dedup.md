# Offer Pairing and Duplicate-PC Prevention

This document captures **observed protocol behavior of the JS WebTorrent / bittorrent-tracker reference** and the corresponding rules our C# implementation must follow to interoperate correctly without forming redundant `RTCPeerConnection`s.

## TL;DR

- A peer announces with up to `numwant` offers in the `offers` array (JS reference caps at **5**, NOT 10).
- The tracker matches incoming offers to candidate peers **positionally** — `peers[i]` gets `params.offers[i]`. Each candidate peer is selected **at most once** per pairing round (random LRU walk; no peer is paired with multiple offers in the same announce).
- The tracker forwards offers ONE-AT-A-TIME via separate `announce` messages (each carrying `peer_id`, `offer`, `offer_id`).
- The CLIENT-SIDE invariant that prevents duplicate PCs is: **never create a second `RTCPeerConnection` to a `remotePeerId` we already have a connection (or pending offer) for.** The JS WebTorrent client does NOT explicitly enforce this at the offer-relay layer — it relies on the BT-handshake-level dedup (which JS WebTorrent also runs) to collapse the duplicates after the fact. In Chromium, that post-PC dedup triggers an SCTP cascade across PCs to the same remote (verified 2026-05-03 in Stable + Canary), so we MUST dedup earlier.

## Reference protocol (bittorrent-tracker JS)

Source: `tracker-debug/node_modules/bittorrent-tracker/`. Captured live in `Docs/protocol-reference/07-full-transcript.md`.

### Numwant cap

`lib/client/websocket-tracker.js:60-69` — JS client caps `numwant = Math.min(opts.numwant, 5)`. The 5 ceiling is hard-coded. Our `MaxOffers = 10` (TrackerSignalingClient.cs:31) and `Numwant = 10` (WebSocketTracker.cs:354) defaults DOUBLE the offer-pool size compared to the reference.

### Server pairing strategy

`server.js:522-535` — when an announce arrives with N offers:

1. `peers = _getPeers(numwant - 1)` — random walk of swarm LRU (excluding self), at most `numwant - 1` candidates.
2. `peers.forEach((peer, i) => peer.socket.send({ ..., offer: params.offers[i].offer, offer_id: params.offers[i].offer_id, peer_id: announcer.peer_id }))`.

Each candidate peer is pulled at most once per round (random LRU walk). The announcer's offer pool is matched **positionally** against the candidate list; surplus offers (when `offers.length > peers.length`) are discarded silently for that round.

### Offer relay shape

Each forwarded offer arrives as a separate WebSocket frame:

```json
{
  "action": "announce",
  "info_hash": "...",
  "peer_id": "<announcer's peer_id>",
  "offer": { "type": "offer", "sdp": "..." },
  "offer_id": "<20 raw bytes binary string>"
}
```

The receiver:
1. Looks up or creates an `RTCPeerConnection` for the offer.
2. Calls `setRemoteDescription` with the SDP, `createAnswer`, `setLocalDescription`.
3. Sends an `announce` back with `to_peer_id = announcer.peer_id`, `answer`, and the same `offer_id`.

### Answer relay shape

`server.js:537-548` — when an answer arrives:

1. Server looks up the target peer by `params.to_peer_id`.
2. Forwards the answer with `peer_id` set to the answerer's id and `offer_id` echoed back.

The original offerer correlates by `offer_id` (kept in its own pending-offer table) and applies the answer to the correct `RTCPeerConnection`.

### Multi-offer-per-pair behavior in practice

Reference behavior in a 2-peer steady state where each side announces with `numwant=5`:

- A's announce → tracker has A's 5 offers parked.
- B announces → tracker forwards up to 5 of A's offers to B (positional pairing). B answers each.
- B's announce ALSO contains B's 5 offers → tracker forwards up to 5 of B's offers to A. A answers each.
- Net result: **up to 10 RTCPeerConnections exist between A and B** (5 from A's offers, 5 from B's). Each pair is a complete WebRTC handshake with its own DTLS/SCTP stack.

The JS reference DOES form these duplicate PCs. JS WebTorrent's BT-handshake-level dedup (matching on canonical BT peer_id post-handshake) collapses them at the BT layer — destroying the loser `Peer` objects. In libwebrtc/Chromium, destroying one PC's data channel after both sides have completed BT handshake **triggers `sctp-failure | User-Initiated Abort | sctpCauseCode=12` on the SURVIVING PC's data channel** (verified Chrome Stable + Chrome Canary, 2026-05-03). Both sides observe the cascade simultaneously and lose all PCs to the canonical remote. RenderMandelbrot in `lostbeard.github.io` reproduces it 100% with coord=Stable, worker=Canary.

## Required client-side behavior to interoperate

To match the reference protocol AND avoid the Chromium SCTP cascade, our client MUST enforce this invariant at the **offer-relay layer**, BEFORE constructing an `RTCPeerConnection`:

> **An offer received from `remotePeerId` X is processed only if we have NO existing or pending RTCPeerConnection for X. Otherwise, the offer is silently dropped.**

Symmetrically, on outbound:

> **When generating offers for the announce pool, we may generate up to `numwant` offers but not more. The tracker's positional-pairing rule guarantees each offer goes to a distinct candidate peer; our duplicate-PC formation comes from the INCOMING side, not the outgoing.**

Numwant should be **5 to match the reference**. Larger values (we currently default 10) double the duplicate-PC formation rate without any benefit — the tracker will just discard surplus offers when fewer candidates exist, and forward 1-to-N pairings when many candidates exist (where N is the incoming peer's count).

## Our implementation gaps (as of 2026-05-03)

### Gap 1: No remote-peer_id dedup on incoming offer

`SpawnDev.WebTorrent.WebSocketTracker.SimplePeerRoomHandler.HandleOfferAsync` (WebSocketTracker.cs:289-328): creates a fresh `SimplePeer` via `_factory(false)` for every incoming offer with NO check against the per-room remote peer set. The non-WebTorrent path `SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler.HandleOfferAsync` (RtcPeerConnectionRoomHandler.cs:107-130) already has the correct pattern: `if (_peers.ContainsKey(remoteHex)) return null; // already paired`. We need the equivalent in the WebTorrent flavor.

### Gap 2: Numwant divergence

Our defaults are 10 (TrackerSignalingClient.cs:31, WebSocketTracker.cs:354). JS reference is 5. Fix: lower to 5 to match.

### Gap 3: Outbound-offer race window

When two peers announce within a tracker pairing window, BOTH may end up generating offers for the same remote (because at offer-generation time we don't know who the offer will be matched with — the tracker decides). Even with Gap 1 fixed on the inbound side, we can still generate 5 outbound offers + receive 5 inbound offers = up to 5 incoming PCs accepted via Gap 1 dedup but the FIRST inbound passes (no dedup state yet) AND all 5 outbound pair with the remote's incoming side.

The mitigation is a **two-phase acceptance**: track `pendingOutbound` peer ids (populated at HandleAnswerAsync time when we know who answered) and add Gap 1 dedup to also check `pendingOutbound`. Combined with the BT-handshake-level dedup as a final safety net, this collapses the duplicate-PC count from 5+5=10 to at most 1.

## Fix landed (3.2.4-rc.5, 2026-05-03)

Three layers, all in `SpawnDev.WebTorrent.WebSocketTracker` (the `SimplePeerRoomHandler` plus the file-level `CrossTrackerDedupRegistry`):

### Layer 1: offer-relay dedup (rc.2)

A `_remotePeerIds` `ConcurrentDictionary<string, byte>` tracks the set of remote peer_ids we have an active or pending `SimplePeer` for. `HandleOfferAsync` `TryAdd`s before creating any PC; if the slot is taken (e.g. an earlier `HandleOfferAsync` or `HandleAnswerAsync` for the same remote got there first) the offer is silently dropped. `HandleAnswerAsync` does the same and disposes the duplicate `SimplePeer` if it loses the race. `peer.OnClose +=` cleans up the slot when the connection ends so re-pairing after a real disconnect still works. Mirrors `SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler.HandleOfferAsync:111`.

### Layer 2: cross-side-stable peer_id tiebreaker (rc.3 first cut → rc.5 correct design)

Layer 1 prevents one peer from creating duplicate PCs to the same remote. But when **both** peers announce within the same tracker pairing window, each side races its `HandleOfferAsync` (incoming offer-relay from the remote's announce) vs its `HandleAnswerAsync` (incoming answer to its own pending offer). The "first runner wins" rule is non-deterministic per side, and when the two sides' coin flips disagree, they end up holding **different halves** of the duplicate pair — A keeps its `as-offerer` PC while B keeps its `as-offerer` PC, and neither pair has both endpoints alive. peerCount stays at 0 on both sides. `P2PSwarm.DemoPath_MandelbrotChunk_OutputOnlyBuffer_OverRealWebRtc_BitExact` failed against rc.2 with exactly this pattern (Coordinator peers: 0, Worker peers: 0 after 60s).

The fix is a lex-compare hex peer_id tiebreaker:

> **The LARGER peer_id is the canonical answerer-side. The SMALLER peer_id is the canonical offerer-side.**

#### rc.3 / rc.4 first cut: WRONG — broke the asymmetric-announce case

rc.3 and rc.4 (cross-tracker variant) gated each handler on the comparison BEFORE the TryAdd:

- `HandleOfferAsync(X for offer from Y)` — accept only if `X.peer_id > Y`. Otherwise drop.
- `HandleAnswerAsync(X for answer from Y)` — accept only if `X.peer_id < Y`. Otherwise dispose pending peer.

This rule failed when only ONE peer announces. With our `WebSocketTracker` doing no periodic re-announce (the announce-interval timer body is intentionally empty), each peer announces ONCE on initial WS connect. In a 2-peer swarm the steady state is **only worker→coord pairing** (worker announced after coord, so the tracker had coord as a candidate; coord's earlier announce had no candidates and its 5 offers were silently dropped by the tracker). For that ONE pairing direction to succeed, coord's `HandleOfferAsync(worker)` MUST accept — but the rc.3+rc.4 rule said "accept only if coord > worker." When coord.peer_id < worker.peer_id (50% probability with random peer_ids), coord dropped the offer-relay; worker's pending offer timed out unanswered; peerCount stayed 0/0. `P2PSwarm.TwoTab_PeerDiscovery` failed deterministically when the random peer_ids ordered the wrong way.

#### rc.5 correct design: first-claim wins, tiebreaker only on conflict

The slot accepts the first arriving handler unconditionally. The tiebreaker fires only when the slot is later claimed by the OTHER path:

- `HandleOfferAsync(X for offer from Y)`:
  - Slot empty → claim type=Offer with the new responder peer. ACCEPT.
  - Slot held by `HandleAnswerAsync` (offerer-side peer wired) → tiebreaker. If X > Y (we are answerer-side per tiebreaker), REPLACE: dispose existing peer, claim. If X < Y, DROP, dispose new peer.
  - Slot held by `HandleOfferAsync` (another tracker accepted first) → DROP, dispose new peer.

- `HandleAnswerAsync(X for answer from Y)` (mirror):
  - Slot empty → claim type=Answer with the pending offerer peer. ACCEPT.
  - Slot held by `HandleOfferAsync` (answerer-side peer accepted) → tiebreaker. If X < Y (we are offerer-side per tiebreaker), REPLACE. If X > Y, DROP.
  - Slot held by `HandleAnswerAsync` → DROP.

**Asymmetric case** (only A announces, A<B or A>B doesn't matter): only one handler per side runs at all, slot always empty when it does, ACCEPT every time. ✓

**Simultaneous case** (both announce, A < B): both sides race their offer-vs-answer paths. Whichever runs first claims the slot; the second hits the tiebreaker. The LARGER peer always converges on answerer-side, the SMALLER on offerer-side, regardless of the per-side race ordering. Both peers end up holding HALVES OF THE SAME PAIR (offerer at smaller, answerer at larger). ✓

#### Implementation notes

`CrossTrackerDedupRegistry` stores `(SlotType type, SimplePeer peer)` per remote. `Release(remoteHex, ownerPeer)` uses `ReferenceEquals` so a stale `OnClose` on a replaced peer doesn't free the new owner's slot. Replace-side cleanup runs the displaced peer's `DisposeAsync` outside the registry lock.

The TryAccept methods take the new SimplePeer at claim time so the slot can stash it atomically (the responder peer is created cheaply via `_factory(false)` BEFORE TryAcceptOffer is called; if the slot rejects us we dispose immediately, paying a SimplePeer allocation but no PC negotiation). Caller checks `out SimplePeer? toDispose`; non-null means the call replaced an existing peer that must be disposed.

`WebSocketTracker` stores `_localPeerIdHex` and passes it through the registry constructor (the local peer_id is needed for the comparison).

### `MaxOffers` / `Numwant` align

Both `TrackerSignalingClient.MaxOffers` and `WebSocketTracker.AnnounceOptions.Numwant` defaults lowered 10 → 5 to match the JS reference cap (`Math.min(opts.numwant, 5)` in `lib/client/websocket-tracker.js:61`). The previous 10 doubled the duplicate-PC formation rate without any benefit because the tracker's positional pairing only matches at most one offer per candidate peer per round; surplus offers were silently discarded.

### Layer 3: cross-tracker registry (rc.4)

`WebTorrentClientOptions.DefaultTrackers` ships TWO tracker URLs by default — `wss://hub.spawndev.com:44365/announce` AND `wss://tracker.openwebtorrent.com`. Every torrent announces to BOTH. When the same logical remote peer is on both trackers (which is the steady state for a SpawnDev swarm where peers register with the full default set), each tracker independently delivers an offer-relay for that peer. Pre-rc.4 the dedup state (`_remotePeerIds` ConcurrentDictionary) lived inside each `SimplePeerRoomHandler`, and each tracker had its OWN handler with its OWN dict, so the per-tracker dedup didn't see the other tracker's claim. Result: the same logical remote produced N PCs (one per tracker subscription) and the rc.1+rc.2+rc.3 pair-of-PCs math compounded by N — a 2-peer swarm with 2 default trackers produced ~20 PCs (out of which only 1 paired into a working data channel) under rc.3, and 40 under pre-rc.2 numwant=10 (verified against the live RenderMandelbrot demo on lostbeard.github.io, 2026-05-03).

rc.4 lifts the dedup state from per-handler to a `CrossTrackerDedupRegistry` keyed on `(info_hash_hex, local_peer_id_hex)`. Every `WebSocketTracker.Subscribe` for the same torrent looks up the SAME registry instance via `CrossTrackerDedupRegistry.GetOrCreate(infoHashHex, localPeerIdHex)`. The handler's `HandleOfferAsync` and `HandleAnswerAsync` now call `_dedup.TryAcceptOffer(remoteHex)` / `_dedup.TryAcceptAnswer(remoteHex)`, which combine the rc.3 cross-side-stable tiebreaker AND the slot-claim TryAdd into one atomic check.

Result: exactly **one SimplePeer / RTCPeerConnection per logical remote peer**, regardless of how many trackers we are subscribed to. The first tracker to deliver an offer-relay for a remote claims the slot; the second tracker's offer-relay drops at `TryAcceptOffer` without creating a PC.

The same registry enforces the rc.3 tiebreaker across trackers — if peer A and peer B race, all of A's tracker-A and tracker-B offer-relays land at the same registry on B's side and the LARGER peer_id wins exactly once. No tracker-to-tracker race can produce a divergent decision.

`Release(remoteHex)` is called from each accepted SimplePeer's `OnClose` so a real disconnect frees the slot for re-pairing.

`WebSocketTracker.ClearPool()` also drops the registry pool to prevent stale slots leaking across test runs / app restarts.

## Server-side check

`SpawnDev.RTC.Server.TrackerSignalingServer` (TrackerSignalingServer.cs:373-400): positional pairing matches the JS reference. NOT the bug. Our server is correct.

## Verification harness

`tracker-debug/verify-tracker-parity.mjs` validates server-side behavior matches JS reference. It does NOT test client-side dedup or duplicate-PC count. To verify Gap 1 + Gap 2 fixes we should add a client-side parity test that runs the C# client against the JS reference server and asserts at most 1 RTCPeerConnection per remote peer_id after a 2-peer pairing round.

## Pointers (file:line)

- Reference: `tracker-debug/node_modules/bittorrent-tracker/lib/client/websocket-tracker.js:60-69` (numwant cap = 5)
- Reference: `tracker-debug/node_modules/bittorrent-tracker/server.js:522-535` (positional pairing)
- Our (correct dedup): `SpawnDev.RTC/SpawnDev.RTC/Signaling/RtcPeerConnectionRoomHandler.cs:107-130`
- Our (missing dedup): `SpawnDev.WebTorrent/SpawnDev.WebTorrent/WebSocketTracker.cs:289-328`
- Our (outbound generator): `SpawnDev.WebTorrent/SpawnDev.WebTorrent/WebSocketTracker.cs:226-287`
- Our (numwant defaults): `SpawnDev.RTC/SpawnDev.RTC/Signaling/TrackerSignalingClient.cs:31`, `SpawnDev.WebTorrent/SpawnDev.WebTorrent/WebSocketTracker.cs:354`
- Server pairing (correct): `SpawnDev.RTC/SpawnDev.RTC.Server/TrackerSignalingServer.cs:373-400`
- BT-level dedup (the post-PC cleanup): `SpawnDev.WebTorrent/SpawnDev.WebTorrent/Torrent.cs:891-997` — this still runs as a safety net but should rarely trigger if Gap 1 is fixed.
