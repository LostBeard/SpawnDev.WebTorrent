# WebTorrent Complete Lifecycle - Order of Operations

This document describes the complete lifecycle of a WebTorrent session from magnet link to completed download. Every state transition, every message, in order. This is the master reference for implementing a WebTorrent client.

## Table of Contents

1. [Overview](#overview)
2. [Phase 1: Client Initialization](#phase-1-client-initialization)
3. [Phase 2: Torrent Creation from Magnet URI](#phase-2-torrent-creation-from-magnet-uri)
4. [Phase 3: Tracker Announce & Peer Discovery](#phase-3-tracker-announce--peer-discovery)
5. [Phase 4: WebRTC Connection Establishment](#phase-4-webrtc-connection-establishment)
6. [Phase 5: BitTorrent Wire Handshake](#phase-5-bittorrent-wire-handshake)
7. [Phase 6: Extension Handshake (BEP 10)](#phase-6-extension-handshake-bep-10)
8. [Phase 7: Metadata Exchange (BEP 9)](#phase-7-metadata-exchange-bep-9)
9. [Phase 8: Piece Exchange](#phase-8-piece-exchange)
10. [Phase 9: Seeding](#phase-9-seeding)
11. [Phase 10: Shutdown](#phase-10-shutdown)
12. [Complete Message Sequence Diagram](#complete-message-sequence-diagram)

---

## Overview

WebTorrent extends BitTorrent to work in web browsers by replacing TCP connections with WebRTC data channels. The tracker serves double duty as both a peer discovery service and a WebRTC signaling relay.

```
Magnet URI -> Parse info_hash
           -> Connect to WebSocket trackers
           -> Announce with WebRTC offers
           -> Tracker relays offers to peers
           -> Peer creates WebRTC answer
           -> Tracker relays answer back
           -> WebRTC data channel opens
           -> BitTorrent wire protocol over data channel
           -> Extended handshake (BEP 10)
           -> Metadata exchange (BEP 9) if magnet
           -> Piece exchange (request/piece)
           -> Download complete -> become seeder
```

---

## Phase 1: Client Initialization

### 1.1 Generate Peer ID

Format: Azureus-style, 20 bytes.

```
-WW0208-xxxxxxxxxxxx
 ^  ^    ^
 |  |    +-- 12 random bytes (ASCII-safe: alphanumeric + symbols)
 |  +------- version (02.08)
 +---------- client ID (WW = WebTorrent)
```

The peer ID is used:
- In tracker announces (as `peer_id` field)
- In the BitTorrent wire handshake (last 20 bytes)
- For self-connection detection (ignore peers with same ID)

### 1.2 Initialize WebSocket Tracker Connections

Default trackers (merged into every torrent):
```
wss://tracker.openwebtorrent.com
wss://tracker.webtorrent.dev
```

WebSocket connections are pooled: one connection per (tracker URL + peer ID) pair, shared across all torrents. This matches the JS WebTorrent `socketPool` pattern.

### 1.3 Initialize ICE Servers

Default STUN servers for WebRTC NAT traversal:
```
stun:stun.l.google.com:19302
stun:global.stun.twilio.com:3478
```

---

## Phase 2: Torrent Creation from Magnet URI

### 2.1 Parse Magnet URI

Extract from the magnet link:
- `xt` (exact topic): `urn:btih:<info_hash_hex>` - the 20-byte SHA-1 info hash
- `dn` (display name): human-readable name (optional)
- `tr` (trackers): announce URLs (merged with defaults)
- `ws` (web seeds): HTTP URLs for BEP 19 web seeding (optional)

### 2.2 Create Torrent Object

At this point we have:
- Info hash (20 bytes)
- Tracker URLs
- Web seed URLs (if any)
- No metadata yet (no piece count, file list, etc.)

The torrent enters "metadata needed" state.

### 2.3 Subscribe to Tracker Connections

For each tracker URL:
1. Get or create a shared WebSocket tracker connection
2. Subscribe to the torrent's info_hash on that connection
3. Register callbacks for peer events, swarm stats, warnings

---

## Phase 3: Tracker Announce & Peer Discovery

### 3.1 Initial Announce

Send a `started` announce to each tracker with WebRTC offers:

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "-WW0208-xxxxxxxxxxxx",
  "uploaded": 0,
  "downloaded": 0,
  "left": -1,
  "event": "started",
  "numwant": 10,
  "offers": [
    {
      "offer": { "type": "offer", "sdp": "v=0\r\n..." },
      "offer_id": "<20-byte binary string>"
    },
    ...
  ]
}
```

**Critical details:**
- `info_hash` and `offer_id` use **binary string encoding** (latin1: each byte becomes a char)
- `peer_id` is ASCII (no encoding needed for standard WebTorrent peer IDs)
- `left` = -1 or null means "unknown" (magnet link, don't know total size yet)
- `numwant` = max peers wanted (JS default: 10, capped at MAX_ANNOUNCE_PEERS)
- Each `offer` contains a pre-generated WebRTC SDP offer with ICE candidates gathered

### 3.2 Offer Generation (Parallel)

For each numwant slot, in parallel (matches JS `Promise.all` pattern):

1. Create a new RTCPeerConnection with ICE servers
2. Create a data channel (initiator role, ordered, reliable)
3. Call `createOffer()`
4. Call `setLocalDescription(offer)`
5. Wait for ICE gathering to complete (all candidates gathered)
6. Read `localDescription.sdp` (now includes ICE candidates)
7. Remove `a=ice-options:trickle` line from SDP
8. Package as `{offer: {type: "offer", sdp: "..."}, offer_id: "<random 20 bytes>"}`
9. Store the peer connection keyed by offer_id (with a 50s timeout)

### 3.3 Tracker Response

The tracker responds with swarm stats:

```json
{
  "action": "announce",
  "info_hash": "<binary string>",
  "interval": 120,
  "complete": 5,
  "incomplete": 2
}
```

- `interval`: re-announce interval in seconds
- `complete`: number of seeders
- `incomplete`: number of leechers

### 3.4 Offer Relay (Tracker to Other Peers)

The tracker relays our offers to other peers in the swarm:

```json
{
  "action": "announce",
  "info_hash": "<binary string>",
  "peer_id": "<our peer_id>",
  "offer": { "type": "offer", "sdp": "v=0\r\n..." },
  "offer_id": "<binary string>"
}
```

### 3.5 Receiving Offers from Other Peers

When another peer's offer is relayed to us:

1. Create a new RTCPeerConnection (responder role)
2. Register `ondatachannel` handler (responder waits for remote data channel)
3. Call `setRemoteDescription({type: "offer", sdp: remote_sdp})`
4. Call `createAnswer()`
5. Call `setLocalDescription(answer)`
6. Wait for ICE gathering to complete
7. Read `localDescription.sdp`
8. Send answer back to tracker:

```json
{
  "action": "announce",
  "info_hash": "<binary string>",
  "peer_id": "<our peer_id>",
  "to_peer_id": "<offering peer's id>",
  "answer": { "type": "answer", "sdp": "v=0\r\n..." },
  "offer_id": "<binary string matching the offer>"
}
```

**Critical:** The answer message MUST NOT include `uploaded`, `downloaded`, `left`, `event`, `numwant`, or `offers`. Only the fields listed above.

### 3.6 Receiving Answers to Our Offers

When an answer to one of our offers arrives:

1. Look up the pending peer connection by `offer_id` (binary string -> hex for lookup)
2. Cancel the 50s timeout timer
3. Call `setRemoteDescription({type: "answer", sdp: answer_sdp})`
4. The WebRTC connection establishment begins (ICE connectivity checks, DTLS handshake)

---

## Phase 4: WebRTC Connection Establishment

### 4.1 ICE Connectivity Checks

After both offer and answer SDPs are set:
1. ICE agents on both sides begin connectivity checks
2. Each candidate pair is tested (host, server-reflexive, relay)
3. The best candidate pair is selected ("nominated")
4. ICE connection state transitions: `new` -> `checking` -> `connected`

### 4.2 DTLS Handshake

After ICE connects:
1. DTLS handshake over the selected ICE candidate pair
2. The offer side is `actpass`, answer side chooses `active` or `passive`
3. Self-signed certificates are used, fingerprints were exchanged in SDP
4. After DTLS completes, SCTP association is established

### 4.3 SCTP & Data Channel

1. SCTP association opens over DTLS
2. The data channel negotiated in the SDP becomes available
3. Data channel opens: `readyState` transitions to `"open"`
4. Both sides can now send binary data

**Data channel properties:**
- Label: random hex string (set by initiator, e.g., `"dc"` or random)
- Ordered: true
- Reliable: true (no maxRetransmits or maxPacketLifeTime)
- Binary type: `arraybuffer`
- Max message size: 262144 bytes (256KB)

---

## Phase 5: BitTorrent Wire Handshake

Once the data channel is open, both peers immediately send the BitTorrent handshake. The handshake is 68 bytes, sent simultaneously by both peers:

```
Byte layout (68 bytes total):
+--------+---------------------+----------+-----------+---------+
| Offset | Field               | Size     | Value     | Notes   |
+--------+---------------------+----------+-----------+---------+
| 0      | pstrlen             | 1 byte   | 19 (0x13) |         |
| 1      | pstr                | 19 bytes | "BitTorrent protocol" |
| 20     | reserved            | 8 bytes  | flags     | See below|
| 28     | info_hash           | 20 bytes | SHA-1     |         |
| 48     | peer_id             | 20 bytes | -WW0208-  |         |
+--------+---------------------+----------+-----------+---------+
```

### Reserved bytes (extension flags):

```
Byte 5, bit 4 (0x10): BEP 10 Extension Protocol support
Byte 7, bit 0 (0x01): BEP 5 DHT support
Byte 7, bit 2 (0x04): BEP 6 Fast Extension support
```

WebTorrent JS typically sets: `0x00 0x00 0x00 0x00 0x00 0x10 0x00 0x05`
(Extended + DHT + Fast)

### Handshake Validation

Each peer validates the received handshake:
1. `pstrlen` must be 19
2. `pstr` must be "BitTorrent protocol"
3. `info_hash` must match the torrent we're connecting for
4. `peer_id` must not match our own (self-connection detection)

If validation fails, the connection is destroyed.

### Post-Handshake State

After both handshakes are exchanged:
- Both peers know each other's extension support (reserved bytes)
- `HasFast = true` if both support BEP 6
- If both support BEP 10 Extended, proceed to Phase 6

---

## Phase 6: Extension Handshake (BEP 10)

If both peers advertised BEP 10 support in the reserved bytes, they exchange an extended handshake immediately after the BT handshake.

### 6.1 Send Extended Handshake

Message format:
```
[4 bytes: length][1 byte: msg_id=20][1 byte: ext_id=0][bencoded payload]
```

The payload is a bencoded dictionary:

```
{
  "m": {
    "ut_metadata": 1,
    "ut_pex": 2,
    "lt_donthave": 3
  },
  "metadata_size": 139,
  "v": "WebTorrent 2.8.3",
  "reqq": 250
}
```

| Key | Type | Description |
|-----|------|-------------|
| `m` | dict | Extension name -> message ID mapping |
| `metadata_size` | int | Total info dict size in bytes (seeder only) |
| `v` | string | Client version string |
| `reqq` | int | Max outstanding requests (default 250) |
| `yourip` | bytes | Remote peer's IP (4 or 16 bytes, optional) |
| `p` | int | Listen port (optional) |

### 6.2 Process Peer's Extended Handshake

When receiving the peer's extended handshake:
1. Parse the bencoded dictionary
2. Store the peer's `m` mapping (extension name -> their message ID)
3. Store `metadata_size` if present (needed for ut_metadata)
4. Notify all registered extensions of the peer's handshake data

**Critical:** Each peer assigns its OWN extension IDs. When sending a message to the peer, use THEIR extension ID for the extension name, not yours.

---

## Phase 7: Metadata Exchange (BEP 9)

If we're joining via magnet link, we need the torrent's info dictionary from a peer.

### 7.1 Check Metadata Availability

After the extended handshake:
- If the peer's handshake includes `metadata_size`, they have the metadata
- If the peer's `m` dict includes `ut_metadata`, they support the protocol

### 7.2 Request Metadata

Send ut_metadata request (message type 0 = request):

```
Wire message: [length][msg_id=20][ext_id=<peer's ut_metadata id>][bencoded payload]
Payload: d8:msg_typei0e5:piecei0ee
```

Metadata is split into 16KB pieces. For small metadata (< 16KB), there's only piece 0.

### 7.3 Receive Metadata

The peer responds with ut_metadata data (message type 1 = data):

```
Payload: d8:msg_typei1e5:piecei0e10:total_sizei139ee<raw info dict bytes>
```

The raw info dictionary bytes follow immediately after the bencoded header dict.

### 7.4 Verify Metadata

After receiving all pieces:
1. Concatenate the raw info dict bytes
2. Compute SHA-1 hash
3. Compare against the `info_hash` from the magnet link
4. If match: metadata verified, parse the info dict
5. If mismatch: reject, try another peer

### 7.5 Parse Info Dict

The verified info dict contains:
- `name`: torrent name
- `piece length`: bytes per piece
- `pieces`: concatenated SHA-1 hashes (20 bytes each)
- `length`: total size (single file) OR `files`: file list (multi-file)
- `files[].length`, `files[].path`: individual file sizes and paths

Now the torrent has full metadata: file list, piece count, piece hashes.

---

## Phase 8: Piece Exchange

### 8.1 Bitfield / Have-All

Immediately after handshake (before or after extended handshake):

**Seeder (has all pieces):**
- If BEP 6 Fast: sends `have_all` (msg_id=0x0E, 1 byte)
- Otherwise: sends `bitfield` with all bits set

**Leecher (has no pieces):**
- If BEP 6 Fast: sends `have_none` (msg_id=0x0F, 1 byte)
- Otherwise: sends nothing (empty bitfield is implied)

**Partial (has some pieces):**
- Sends `bitfield` with appropriate bits set

### 8.2 Interest / Choke Negotiation

After receiving the peer's bitfield:

1. **Leecher** checks if peer has pieces we need
2. If yes: send `interested` (msg_id=2)
3. **Seeder** decides to unchoke: send `unchoke` (msg_id=1)
4. Now the leecher can request pieces

### 8.3 Piece Requests

The leecher sends `request` messages:

```
[length=13][msg_id=6][piece_index:uint32][block_offset:uint32][block_length:uint32]
```

- Pieces are subdivided into 16KB blocks (16384 bytes)
- Multiple requests are pipelined (JS default: up to 5 outstanding)
- Rarest-first piece selection for optimal swarm health
- Endgame mode: when few pieces remain, request from multiple peers

### 8.4 Piece Data

The seeder responds with `piece` messages:

```
[length=9+N][msg_id=7][piece_index:uint32][block_offset:uint32][data:N bytes]
```

### 8.5 Piece Verification

After receiving all blocks for a piece:
1. Assemble the complete piece from blocks
2. Compute hash (SHA-1 for v1, SHA-256 for v2)
3. Compare against the piece hash from the info dict
4. If valid: store piece, send `have` to all connected peers
5. If invalid: discard, ban the peer, re-request from another peer

### 8.6 Download Progress

As pieces complete:
- Update bitfield
- Send `have` messages to all peers
- Update downloaded/uploaded counters
- Check if download is complete

---

## Phase 9: Seeding

### 9.1 Download Complete

When all pieces are verified:
1. Send `completed` announce to trackers (with `numwant=50` - want to discover leechers)
2. Send `not_interested` to peers we were downloading from
3. Continue accepting incoming connections and serving piece requests

### 9.2 Serving Pieces

As a seeder:
1. Accept incoming wire connections
2. Send `have_all` or bitfield
3. Wait for `interested` from peers
4. Unchoke peers (rotation algorithm: unchoke top 4 uploaders + 1 random)
5. Respond to `request` messages with `piece` data

### 9.3 Re-Announce

Periodically re-announce to trackers (default: every 120 seconds) with updated stats:
- `uploaded`: total bytes uploaded
- `downloaded`: total bytes downloaded
- `left`: 0 (we have everything)

---

## Phase 10: Shutdown

### 10.1 Leave Swarm

Send `stopped` announce to each tracker:

```json
{
  "action": "announce",
  "info_hash": "<binary string>",
  "peer_id": "<our peer_id>",
  "uploaded": 12345,
  "downloaded": 67890,
  "left": 0,
  "event": "stopped",
  "numwant": 0
}
```

**Critical:** `numwant` MUST be 0 and `offers` MUST NOT be included with `stopped` event.

### 10.2 Close Connections

1. Close all WebRTC data channels
2. Close all RTCPeerConnection objects
3. Close WebSocket tracker connections
4. Dispose timers and cleanup

---

## Complete Message Sequence Diagram

```
Client A (Leecher)          Tracker           Client B (Seeder)
    |                          |                     |
    |--- WS Connect ---------->|                     |
    |                          |<--- WS Connect -----|
    |                          |                     |
    |--- Announce (started) -->|                     |
    |    + 10 WebRTC offers    |                     |
    |                          |                     |
    |<-- Announce response ----|                     |
    |    (complete=5, inc=2)   |                     |
    |                          |                     |
    |                          |--- Offer relay ---->|
    |                          |    (our offer SDP)  |
    |                          |                     |
    |                          |<-- Answer ----------|
    |                          |    (answer SDP)     |
    |                          |                     |
    |<-- Answer relay ---------|                     |
    |                          |                     |
    |========= ICE + DTLS + SCTP ===================>|
    |========= Data Channel Opens ==================>|
    |                          |                     |
    |<============ BT Handshake ===================>|
    |  (68 bytes each direction, simultaneous)       |
    |                          |                     |
    |<============ Extended Handshake ==============>|
    |  (BEP 10, bencoded m dict)                     |
    |                          |                     |
    |<----------- have_all (BEP 6) -----------------|
    |                          |                     |
    |------------ interested ----------------------->|
    |                          |                     |
    |<----------- unchoke ----------------------------|
    |                          |                     |
    |--- ut_metadata request -|                     |
    |   (BEP 9, piece 0)      +-------------------->|
    |                          |                     |
    |<------- ut_metadata data ----------------------|
    |   (BEP 9, piece 0 + raw info dict)             |
    |                          |                     |
    |  [verify SHA-1 of info dict = info_hash]       |
    |  [parse info dict: files, pieces, hashes]      |
    |                          |                     |
    |--- request (piece 0, offset 0, len 16384) --->|
    |--- request (piece 0, offset 16384, len 16384)->|
    |--- request (piece 1, offset 0, len 16384) --->|
    |  ... (pipelined requests)                      |
    |                          |                     |
    |<-- piece (0, 0, data) -------------------------|
    |<-- piece (0, 16384, data) ---------------------|
    |<-- piece (1, 0, data) -------------------------|
    |  ... (piece responses)                         |
    |                          |                     |
    |  [verify piece hash]                           |
    |  [store verified piece]                        |
    |                          |                     |
    |------------ have (piece 0) ------------------>|
    |------------ have (piece 1) ------------------>|
    |  ...                                           |
    |                          |                     |
    |  [all pieces received and verified]            |
    |                          |                     |
    |--- Announce (completed)->|                     |
    |    numwant=50            |                     |
    |                          |                     |
    |------------ not_interested ------------------>|
    |                          |                     |
    |  [now seeding]                                 |
```

---

## Binary String Encoding

The WebSocket tracker protocol uses "binary string" encoding for `info_hash` and `offer_id` fields:

**Encoding (bytes -> JSON string):**
Each byte value (0x00-0xFF) becomes a character with that char code (latin1/ISO-8859-1).

```
bytes: [0x86, 0x3e, 0x15, 0xae, ...]
string: "\u0086>\u0015\u00ae..."
```

**C# implementation:**
```csharp
string ToBinaryString(byte[] bytes)
    => new string(bytes.Select(b => (char)b).ToArray());
```

**JSON serialization quirk:**
System.Text.Json escapes C1 control characters (0x80-0x9F) as `\u00XX`, but JS `JSON.stringify` does not. Use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` and post-process to unescape these.

---

## Timing Reference

Typical timing for a small torrent (49KB, 3 pieces, localhost):

| Phase | Time | Notes |
|-------|------|-------|
| WS connect | +0ms | WebSocket to tracker |
| Announce sent | +14ms | With 5 offers |
| Announce response | +15ms | Swarm stats |
| Offer relayed | +15ms | To other peer |
| Answer received | +3500ms | ICE gathering takes ~3s |
| Data channel open | +3585ms | DTLS + SCTP setup |
| Wire handshake | +3586ms | Immediate after DC open |
| Extended handshake | +3586ms | Same millisecond |
| Metadata received | +3588ms | ut_metadata request/response |
| Download complete | +3590ms | 3 pieces, ~49KB |
| Total | ~3.6s | Dominated by ICE gathering |

---

## References

- BEP 3: The BitTorrent Protocol Specification
- BEP 5: DHT Protocol
- BEP 6: Fast Extension
- BEP 9: Extension for Peers to Send Metadata Files (ut_metadata)
- BEP 10: Extension Protocol
- BEP 11: Peer Exchange (PEX)
- BEP 15: UDP Tracker Protocol
- BEP 17: HTTP Seeding
- BEP 19: WebSeed - HTTP/FTP Seeding (GetRight style)
- BEP 23: Tracker Returns Compact Peer Lists
- BEP 44: Storing Arbitrary Data in the DHT
- BEP 46: Updating Torrents via DHT Mutable Items
- BEP 48: Tracker Protocol Extension: Scrape
- BEP 53: Magnet URI Extension - Select Specific File Indices
- WebTorrent specification: webtorrent.io
