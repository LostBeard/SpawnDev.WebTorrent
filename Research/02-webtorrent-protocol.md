# WebTorrent Protocol - Comprehensive Reference

Everything that makes WebTorrent different from standard BitTorrent, documented for
implementors building a WebTorrent-compatible client from scratch.

**Status:** No official BEP exists for the WebTorrent protocol. The canonical reference
is the `bittorrent-tracker` npm package source code. This document is based on:
live protocol captures, the JS reference implementation source, the SpawnDev.WebTorrent
C# implementation, and community documentation.

**Version:** April 2026

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [WebSocket Tracker Protocol](#2-websocket-tracker-protocol)
3. [WebRTC Data Channel Setup](#3-webrtc-data-channel-setup)
4. [Data Channel Wire Protocol](#4-data-channel-wire-protocol)
5. [Implementation Pitfalls](#5-implementation-pitfalls)
6. [Complete Session Walkthrough](#6-complete-session-walkthrough)
7. [References](#7-references)

---

## 1. Architecture Overview

### What WebTorrent Changes

Standard BitTorrent uses TCP (and optionally uTP/UDP) for peer connections and
HTTP/UDP for tracker communication. WebTorrent adds two things:

1. **WebRTC data channels** as a peer transport (alongside TCP/uTP)
2. **WebSocket trackers** that act as WebRTC signaling servers

Once a WebRTC data channel is open between two peers, the wire protocol is
**identical** to standard BitTorrent - same handshake, same message IDs, same
piece exchange. The only differences are in how peers discover each other and
establish connections.

### The Signaling Problem

WebRTC requires an out-of-band signaling mechanism to exchange SDP offers and
answers before a direct peer connection can be established. WebTorrent solves
this by repurposing BitTorrent trackers as signaling relays:

```
Peer A                    Tracker                   Peer B
  |                         |                         |
  |-- announce + offers --> |                         |
  |                         |-- relay offer --------> |
  |                         |                         |
  |                         | <------ answer ---------|
  | <--- relay answer ------|                         |
  |                         |                         |
  |<======= WebRTC data channel established =========>|
  |                         |                         |
  |<======= BT wire protocol over data channel =====>|
```

### Peer Types

- **Web peer** - Browser-based client. Can ONLY use WebRTC. Cannot use TCP/UDP.
- **Hybrid peer** - Desktop/Node.js client. Can use TCP, UDP, AND WebRTC.
- **Traditional peer** - Standard BitTorrent client. TCP/UDP only. Cannot talk to web peers.

A hybrid peer bridges the web and traditional swarms, making all peers
accessible to each other through piece relay.

---

## 2. WebSocket Tracker Protocol

### 2.1 Connection

The client connects to the tracker via WebSocket. The URL uses `ws://` or `wss://`
scheme. All messages are JSON text frames (not binary).

```
ws://tracker.example.com/announce
wss://tracker.example.com/announce
```

A single WebSocket connection is shared across all torrents on that tracker.
The JS reference implementation maintains a socket pool keyed by
`(trackerUrl + peerId)` - one connection per tracker per client identity.

### 2.2 Binary String Encoding

**This is the single most common source of interoperability bugs.**

The `info_hash`, `offer_id`, and sometimes `peer_id` fields contain raw binary
data (20 bytes each). In the JSON messages, these are encoded as **latin1 binary
strings** - each byte value becomes a character with that code point.

This matches the JavaScript pattern:

```javascript
// JS: hex string to binary string
function hex2bin(hex) {
  return hex.match(/../g).map(h => String.fromCharCode(parseInt(h, 16))).join('');
}

// JS: binary string to hex
function bin2hex(str) {
  return str.split('').map(c => c.charCodeAt(0).toString(16).padStart(2, '0')).join('');
}
```

**Example:** Info hash `863e15ae3ac365c56bfbd1139401ece3a55f8422` becomes
a 20-character string where:
- Byte 0x86 becomes char U+0086
- Byte 0x3e becomes char '>' (U+003E)
- Byte 0x15 becomes char U+0015
- ... and so on

In C#:

```csharp
// Bytes to binary string (latin1)
public static string ToBinaryString(byte[] bytes)
    => new string(bytes.Select(b => (char)b).ToArray());

// Binary string back to bytes
public static byte[] FromBinaryString(string s)
    => s.Select(c => (byte)c).ToArray();

// Hex to binary string
public static string HexToBinaryString(string hex)
    => ToBinaryString(Convert.FromHexString(hex));
```

**Critical detail:** The `peer_id` is typically ASCII-safe (e.g., `-WW0208-xxxxxxxxxxxx`)
and does not need special encoding. But `info_hash` and `offer_id` are raw SHA-1
hashes that routinely contain non-ASCII bytes and MUST be binary-string encoded.

### 2.3 JSON Serialization Quirks

JavaScript's `JSON.stringify()` writes binary string characters as literal UTF-8 bytes.
Most non-JS JSON serializers escape characters in the 0x80-0x9F range (C1 control
characters) as `\u00XX` sequences. WebTorrent trackers expect the literal bytes,
not the escape sequences.

**The fix for C#/.NET:**

1. Use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` to prevent escaping most characters
2. Post-process the JSON to replace remaining `\u00XX` escapes (0x80-0xFF) with literal chars

```csharp
// Step 1: Base JSON options
var opts = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// Step 2: Serialize
var json = JsonSerializer.Serialize(message, opts);

// Step 3: Fix C1 control chars (0x80-0x9F) that System.Text.Json still escapes
var sb = new StringBuilder(json.Length);
for (int i = 0; i < json.Length; i++)
{
    if (i + 5 < json.Length && json[i] == '\\' && json[i + 1] == 'u'
        && json[i + 2] == '0' && json[i + 3] == '0')
    {
        var hex = json.Substring(i + 4, 2);
        if (int.TryParse(hex, NumberStyles.HexNumber, null, out int val) && val >= 0x80)
        {
            sb.Append((char)val);
            i += 5; // skip 6-char \u00XX sequence
            continue;
        }
    }
    sb.Append(json[i]);
}
return sb.ToString();
```

**Why this matters:** If you send `\u0086` instead of the literal byte 0x86 in the
`info_hash` field, the tracker will treat it as a different swarm. Your client will
never find peers.

### 2.4 Message Types

All messages have `"action": "announce"`. The message type is determined by which
fields are present. The tracker distinguishes messages by field presence, not by
any explicit type field.

#### 2.4.1 Announce (Client to Tracker)

Sent when joining a swarm, re-announcing, reporting completion, or leaving.

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "<20-char peer ID string>",
  "uploaded": 0,
  "downloaded": 0,
  "left": 49152,
  "event": "started",
  "numwant": 5,
  "offers": [
    {
      "offer": { "type": "offer", "sdp": "v=0\r\no=..." },
      "offer_id": "<20-byte binary string>"
    }
  ]
}
```

**Field reference:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `action` | string | yes | Always `"announce"` |
| `info_hash` | binary string | yes | 20-byte torrent info hash, latin1 encoded |
| `peer_id` | string | yes | 20-char peer ID (e.g., `-WW0208-xxxxxxxxxxxx`) |
| `uploaded` | number | yes | Total bytes uploaded for this torrent |
| `downloaded` | number | yes | Total bytes downloaded for this torrent |
| `left` | number | conditional | Bytes remaining. Omit or `null` for unknown (magnet) |
| `event` | string | conditional | `"started"`, `"completed"`, or `"stopped"` |
| `numwant` | number | yes | Number of peers wanted (also number of offers sent) |
| `offers` | array | conditional | WebRTC SDP offers (see below) |
| `trackerid` | string | conditional | Echo back if tracker provided one |

**Rules for `event` values:**

| Event | Meaning | Include offers? | numwant |
|-------|---------|-----------------|---------|
| `"started"` | Joining swarm, want peers | YES | Number of offers generated |
| `"completed"` | Finished downloading, now seeding | NO | 0 or small value |
| `"stopped"` | Leaving swarm | NO | 0 |
| (omitted) | Re-announce / update | YES | Number of offers generated |

**Critical rules:**
- Do NOT include `offers` with `"stopped"` or `"completed"` events
- Do NOT include extra fields the tracker does not expect - some trackers silently fail
- The `offers` array length SHOULD equal `numwant`
- JS WebTorrent caps `numwant` at 10 (`MAX_ANNOUNCE_PEERS`)

**Each offer in the `offers` array:**

```json
{
  "offer": {
    "type": "offer",
    "sdp": "v=0\r\no=- 1234567890 0 IN IP4 127.0.0.1\r\n..."
  },
  "offer_id": "<20-byte binary string>"
}
```

The `offer_id` is 20 random bytes, binary-string encoded. It uniquely identifies
this offer so the tracker can route the answer back to the correct pending
peer connection.

#### 2.4.2 Announce Response (Tracker to Client)

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "interval": 120,
  "complete": 2,
  "incomplete": 1
}
```

| Field | Type | Description |
|-------|------|-------------|
| `action` | string | `"announce"` |
| `info_hash` | binary string | Echo of the info hash |
| `interval` | number | Re-announce interval in **seconds** |
| `complete` | number | Number of seeders in this swarm |
| `incomplete` | number | Number of leechers in this swarm |

**Note:** The `interval` field is in seconds. Multiply by 1000 for milliseconds.
Typical value: 120 (2 minutes).

#### 2.4.3 Offer Relay (Tracker to Client)

When the tracker receives offers from Peer A, it relays one offer to each
other peer in the swarm (up to `numwant` peers).

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "<offering peer's ID>",
  "offer": {
    "type": "offer",
    "sdp": "v=0\r\no=..."
  },
  "offer_id": "<20-byte binary string>"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `action` | string | `"announce"` |
| `info_hash` | binary string | Info hash for routing to correct torrent |
| `peer_id` | string | The offering peer's ID (NOT yours) |
| `offer` | object | `{type: "offer", sdp: "..."}` |
| `offer_id` | binary string | Must be echoed in the answer |

**How to distinguish this from an announce response:** Check for the `offer` field.
If present, this is an offer relay. The JS reference checks `offer` and `answer`
field presence rather than using a separate action value.

#### 2.4.4 Answer (Client to Tracker)

Sent in response to a received offer. The tracker relays this to the offering peer.

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "<your peer ID>",
  "to_peer_id": "<offering peer's ID>",
  "answer": {
    "type": "answer",
    "sdp": "v=0\r\no=..."
  },
  "offer_id": "<20-byte binary string>"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `action` | string | `"announce"` |
| `info_hash` | binary string | Info hash |
| `peer_id` | string | YOUR peer ID |
| `to_peer_id` | string | Target peer's ID (from the offer relay) |
| `answer` | object | `{type: "answer", sdp: "..."}` |
| `offer_id` | binary string | MUST match the offer's `offer_id` |
| `trackerid` | string | Optional, include if tracker provided one |

**Critical:** The answer message MUST NOT include `uploaded`, `downloaded`, `left`,
`event`, `numwant`, or `offers`. Only the fields listed above. Including extra
fields can cause trackers to misinterpret the message as a regular announce.

#### 2.4.5 Answer Relay (Tracker to Client)

Sent to the original offerer with the answering peer's response.

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "<answering peer's ID>",
  "answer": {
    "type": "answer",
    "sdp": "v=0\r\no=..."
  },
  "offer_id": "<20-byte binary string>"
}
```

The offering client matches the `offer_id` to its pending offer map, retrieves
the associated `SimplePeer` / `RTCPeerConnection`, and feeds the answer SDP to it.

### 2.5 Offer Generation Flow

When a client announces with offers, it must pre-generate WebRTC offers before
sending the announce message. The flow:

```
For each offer (1..numwant):
  1. Create new RTCPeerConnection
  2. Create data channel (initiator)
  3. createOffer() -> setLocalDescription()
  4. Wait for ICE gathering to complete (non-trickle)
  5. Read localDescription.sdp (now contains ICE candidates)
  6. Generate random 20-byte offer_id
  7. Store (peer, timeout, infoHash) in pending offers map, keyed by offer_id hex
  8. Set timeout to destroy peer if no answer arrives (50 seconds in JS)

Send all offers in the announce message.
```

The JS reference generates all offers in parallel (`Promise.all`).

### 2.6 Answer Processing Flow

When a client receives an offer relay from the tracker:

```
1. Create new RTCPeerConnection (responder - NOT initiator)
2. Subscribe to OnSignal - when an answer is generated, send it to tracker
3. Feed the offer SDP to the peer via Signal({type: "offer", sdp: ...})
4. The peer creates an answer internally:
   a. setRemoteDescription(offer)
   b. createAnswer()
   c. setLocalDescription(answer)
   d. Wait for ICE gathering (non-trickle)
   e. Emit signal with answer SDP
5. Send the answer to the tracker with the same offer_id
```

### 2.7 numwant Behavior

The `numwant` field serves double duty in the WebSocket tracker protocol:

1. **Standard meaning:** How many peers the client wants to know about
2. **WebTorrent meaning:** How many WebRTC offers the client is sending

The tracker distributes offers round-robin to other peers in the swarm. If there
are fewer peers than offers, extra offers go unused and time out.

**JS WebTorrent defaults:**
- `MAX_ANNOUNCE_PEERS = 10` - maximum offers per announce
- Default numwant for regular announces: 10
- numwant for "completed" event: 50 (wants to discover leechers, but no offers)
- numwant for "stopped" event: 0

### 2.8 Re-announce and Reconnection

**Re-announce interval:** The tracker returns an `interval` field (in seconds).
The client should re-announce (with new offers) at this interval. Typical: 120 seconds.

**Reconnection (exponential backoff):**

```
retries = retries + 1
delay = random(0..RECONNECT_VARIANCE) + min(2^retries * RECONNECT_MINIMUM, RECONNECT_MAXIMUM)

Constants (matching JS):
  RECONNECT_MINIMUM = 10,000 ms (10s)
  RECONNECT_MAXIMUM = 3,600,000 ms (1 hour)
  RECONNECT_VARIANCE = 300,000 ms (5 minutes)
```

### 2.9 Tracker as Signaling Server

The WebSocket tracker serves three roles:

1. **Peer discovery** - Standard tracker role: returns swarm stats (seeders/leechers)
2. **Offer relay** - Forwards WebRTC offers from one peer to another
3. **Answer relay** - Forwards WebRTC answers back to the offering peer

After the WebRTC connection is established, the tracker is no longer involved
in that peer-to-peer communication. The data channel carries all BitTorrent
wire protocol messages directly.

---

## 3. WebRTC Data Channel Setup

### 3.1 ICE Gathering Mode

WebTorrent uses **non-trickle ICE** by default. All ICE candidates are gathered
BEFORE the SDP is sent. This means:

- The SDP offer/answer contains all ICE candidates inline
- No separate ICE candidate signaling messages are needed
- The `a=ice-options:trickle` line is REMOVED from the SDP before sending

This is critical because the tracker relay is one-shot - there is no mechanism
to send follow-up ICE candidates after the initial offer/answer exchange.

The JS `simple-peer` library's `filterTrickle()` function:

```javascript
// Remove trickle ICE option from SDP
sdp = sdp.replace(/a=ice-options:trickle\s*\n/g, '')
```

In C#:

```csharp
protected static string FilterTrickle(string sdp)
    => Regex.Replace(sdp, @"a=ice-options:trickle\s*\r?\n?", "");
```

### 3.2 SDP Offer Format

A WebTorrent SDP offer creates a data-channel-only connection. No audio or video
media lines. Here is a complete real SDP offer captured from a live session:

```
v=0
o=- 1234567890 0 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic:WMS *
a=fingerprint:sha-256 57:90:89:53:7A:DE:03:06:0C:C0:2D:21:C7:2A:03:73:CD:37:F0:6C:2C:8C:81:7E:53:FF:FB:FB:60:38:04:2B
m=application 55204 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 192.168.1.120
a=mid:0
a=sendrecv
a=sctp-port:5000
a=max-message-size:262144
a=setup:actpass
a=ice-ufrag:TC8V
a=ice-pwd:JpeiYW9htg3PQxpDvxZSc0
a=candidate:1 1 UDP 2114977791 192.168.1.120 55204 typ host
a=candidate:2 1 UDP 1678769919 64.246.234.108 55204 typ srflx raddr 0.0.0.0 rport 0
a=end-of-candidates
```

### 3.3 SDP Line-by-Line Reference

**Session-level attributes:**

| Line | Meaning | Notes |
|------|---------|-------|
| `v=0` | SDP version | Always 0 |
| `o=- {id} 0 IN IP4 127.0.0.1` | Origin | Session ID varies. See section 3.5 for differences |
| `s=-` | Session name | Always `-` |
| `t=0 0` | Timing | Permanent session |
| `a=group:BUNDLE 0` | Bundle all media on one transport | Required for single-transport WebRTC |
| `a=msid-semantic:WMS *` | Media stream identification | Standard WebRTC |
| `a=fingerprint:sha-256 XX:XX:...` | DTLS certificate fingerprint | 32 hex pairs, colon-separated |

**Media-level attributes (data channel):**

| Line | Meaning | Notes |
|------|---------|-------|
| `m=application {port} UDP/DTLS/SCTP webrtc-datachannel` | Data channel media line | Port is the candidate port |
| `c=IN IP4 {addr}` | Connection address | Typically the host candidate IP |
| `a=mid:0` | Media ID for BUNDLE | Always `0` for single data channel |
| `a=sendrecv` | Direction | Data channels are bidirectional |
| `a=sctp-port:5000` | SCTP port for data channels | Always 5000 |
| `a=max-message-size:262144` | Max SCTP message size | 256 KB. If absent, assume 64 KB |
| `a=setup:actpass` | DTLS role in OFFER | Offerer uses `actpass` (can be either role) |
| `a=setup:active` | DTLS role in ANSWER | Answerer uses `active` (client role) |
| `a=ice-ufrag:{frag}` | ICE username fragment | Short random string |
| `a=ice-pwd:{pwd}` | ICE password | Longer random string |
| `a=candidate:...` | ICE candidate | One per candidate (host, srflx, relay) |
| `a=end-of-candidates` | End of candidate list | Required for non-trickle ICE |

### 3.4 SDP Answer Format

The answer mirrors the offer structure but with key differences:

```
v=0
o=- 9876543210 0 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic:WMS *
a=fingerprint:sha-256 AA:BB:CC:DD:...
m=application 44300 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 192.168.1.121
a=mid:0
a=sendrecv
a=sctp-port:5000
a=max-message-size:262144
a=setup:active
a=ice-ufrag:Kx9P
a=ice-pwd:rA7mBk4Hpz9q2JnKxEfW3w
a=candidate:1 1 UDP 2114977791 192.168.1.121 44300 typ host
a=end-of-candidates
```

**Key differences from offer:**
- `a=setup:active` instead of `a=setup:actpass` - the answerer takes DTLS client role
- Different `a=fingerprint` - each peer has its own DTLS certificate
- Different ICE credentials (`ice-ufrag`, `ice-pwd`)
- Different candidates (answerer's network addresses)

### 3.5 Browser WebRTC vs SipSorcery SDP Differences

This section documents every known SDP difference between browser-generated SDPs
and SipSorcery-generated SDPs. These differences are critical for cross-platform
WebTorrent implementations.

#### Origin Line (`o=`)

| Implementation | Format | Example |
|---------------|--------|---------|
| Chrome/Browser | `o=- {sessionId} {version} IN IP4 {addr}` | `o=- 4611731400430051336 2 IN IP4 127.0.0.1` |
| SipSorcery | `o=rtc {sessionId} 0 IN IP4 127.0.0.1` | `o=rtc 3374675295 0 IN IP4 127.0.0.1` |
| Firefox | `o=mozilla...THIS_IS_SDPARTA {ver} 0 IN IP4 0.0.0.0` | Varies by version |

The origin line username field differs: browsers use `-`, SipSorcery uses `rtc`.
This is cosmetic and does not affect interoperability.

#### ICE Options

| Implementation | Behavior |
|---------------|----------|
| Chrome | Includes `a=ice-options:trickle` in raw SDP; WebTorrent's `filterTrickle()` removes it |
| SipSorcery | Includes `a=ice-options:ice2,trickle` - supports both ICE and trickle |
| After filtering | Neither should have `a=ice-options:trickle` in the final sent SDP |

#### Fingerprint Placement

| Implementation | Placement |
|---------------|-----------|
| Chrome | Session-level (before `m=` line) |
| SipSorcery | Session-level (before `m=` line) |
| Firefox | Can be either session-level or media-level |

Both placements are valid per RFC. Implementations must check both locations.

#### Candidate Format

| Implementation | Format |
|---------------|--------|
| Chrome | `a=candidate:{foundation} 1 {proto} {priority} {addr} {port} typ {type} [raddr {addr} rport {port}]` |
| SipSorcery | Same format, but foundation is always a small integer (1, 2, ...) |
| Firefox | Similar, but uses different foundation hash algorithm |

SipSorcery generates candidates with simple sequential foundation values.
Browser foundations are typically longer hash strings.

#### Candidate Priority Values

| Candidate Type | Chrome Priority (typical) | SipSorcery Priority |
|---------------|--------------------------|---------------------|
| host | ~2113937151 | 2114977791 |
| srflx | ~1677729535 | 1678769919 |
| relay | ~41819903 | Varies |

The exact values differ but follow the same RFC 8445 formula. Both are valid.

#### `a=msid-semantic` Line

| Implementation | Presence |
|---------------|----------|
| Chrome | Present: `a=msid-semantic: WMS *` (note the space after colon) |
| SipSorcery | Present: `a=msid-semantic:WMS *` (no space after colon) |
| Firefox | May be absent for data-channel-only connections |

Both formats are valid. Some parsers are strict about whitespace here.

#### `a=sendrecv` Line

| Implementation | Presence |
|---------------|----------|
| Chrome | Present |
| SipSorcery | Present |
| Firefox | May use `a=inactive` for data channels |

#### Additional Chrome-only attributes

Chrome may include attributes not present in SipSorcery SDPs:

```
a=ice-options:trickle                    (removed by filterTrickle)
a=extmap-allow-mixed                     (safe to ignore)
a=extmap:... urn:ietf:params:rtp-hdrext  (not relevant to data channels)
```

#### SDP Attribute Ordering

**No specific ordering is required.** Per RFC 4566, session-level attributes can
appear in any order, and media-level attributes can appear in any order after
the `m=` line. However:

- The `m=` line MUST come after all session-level attributes
- The `c=` line SHOULD follow immediately after `m=`
- ICE candidates SHOULD come after ICE credentials

Some implementations are stricter than the spec. When in doubt, follow the order
shown in the examples in this document - it matches the most common browser output.

### 3.6 Data Channel Configuration

WebTorrent creates a single data channel per peer connection with these properties:

| Property | Value | Notes |
|----------|-------|-------|
| Label | Random 40-char hex string (initiator) | JS `simple-peer`: `randombytes(20).toString('hex')` |
| Ordered | true | Default - guarantees in-order delivery |
| Reliable | true | Default - guarantees delivery (no maxRetransmits/maxPacketLifeTime) |
| Protocol | (empty) | No sub-protocol negotiation |

**The `simple-peer` library defaults:**

```javascript
// simple-peer channel defaults:
channelName: randombytes(20).toString('hex'),  // initiator generates random name
channelConfig: {},                              // empty = ordered + reliable
```

The responder (non-initiator) does NOT create a data channel. It waits for the
`ondatachannel` event when the remote peer's data channel arrives.

**Critical:** The data channel label does NOT need to be `"dc"` (this is a common
misconception). The JS `simple-peer` library generates a random hex string as
the default channel name. WebTorrent itself does not depend on any specific
channel label - it uses whatever channel is created.

### 3.7 ICE Candidate Types

WebTorrent peers typically include these candidate types:

| Type | Description | Priority |
|------|-------------|----------|
| `host` | Local network address | Highest (~2.1 billion) |
| `srflx` | Server reflexive (NAT-mapped public IP via STUN) | Medium (~1.7 billion) |
| `relay` | TURN relay (if configured) | Lowest (~42 million) |

**Default STUN servers used by WebTorrent:**

```
stun:stun.l.google.com:19302
stun:global.stun.twilio.com:3478
```

When implementing non-trickle ICE, you must wait for all candidates to be gathered
before sending the SDP. Detection methods:

1. **Browser:** `onicecandidate` fires with `null` candidate when gathering completes
2. **SipSorcery:** `onicegatheringstatechange` fires with `complete` state
3. **Timeout fallback:** 2 seconds of candidate silence = assume done (JS `simple-peer` pattern)
4. **Absolute timeout:** 5 seconds (JS `ICE_COMPLETE_TIMEOUT`)

### 3.8 SCTP Configuration

The SCTP layer carries the data channel:

| Parameter | Value | SDP Attribute |
|-----------|-------|---------------|
| SCTP port | 5000 | `a=sctp-port:5000` |
| Max message size | 262144 bytes (256 KB) | `a=max-message-size:262144` |

**Important note on max message size:** If `a=max-message-size` is absent from
the remote SDP, assume 64 KB (the RFC default). A value of 0 means unlimited.
The BitTorrent wire protocol rarely sends messages larger than 16 KB (one block)
plus header overhead, so the 256 KB limit is more than sufficient.

### 3.9 DTLS and Security

Each peer generates an ephemeral DTLS certificate per RTCPeerConnection. The
SHA-256 fingerprint of this certificate is included in the SDP:

```
a=fingerprint:sha-256 57:90:89:53:7A:DE:03:06:0C:C0:2D:21:C7:2A:03:73:CD:37:F0:6C:2C:8C:81:7E:53:FF:FB:FB:60:38:04:2B
```

During the DTLS handshake, each peer verifies that the remote certificate
fingerprint matches what was in the SDP. This provides authentication -
only the peer that created the SDP can complete the DTLS handshake.

The `a=setup` attribute determines DTLS client/server roles:
- `actpass` (offer) - can be either client or server
- `active` (answer) - will be the DTLS client
- `passive` - will be the DTLS server (rarely used in WebTorrent)

---

## 4. Data Channel Wire Protocol

### 4.1 Transport Mapping

Once the WebRTC data channel is open, the standard BitTorrent wire protocol
runs directly over it. The data channel replaces TCP as the byte stream transport.

```
Standard BitTorrent:    TCP socket -> BT wire protocol
WebTorrent:             WebRTC data channel -> BT wire protocol
```

### 4.2 Message Framing

BitTorrent wire protocol messages are length-prefixed:

```
[4 bytes: message length (big-endian uint32)][message_id (1 byte)][payload...]
```

Over TCP, the client must reassemble messages from the byte stream because TCP
delivers arbitrary-sized chunks. Over WebRTC data channels, each `onmessage`
event delivers a complete SCTP message. However, **you still need the length
prefix framing** because:

1. The BT handshake has a different format (not length-prefixed the same way)
2. Multiple BT messages can be batched into a single data channel message
3. A single BT message can span multiple data channel messages
4. Compatibility with TCP transport (same framing code for both)

In practice, the JS WebTorrent reference implementation uses the same framing
parser for both TCP and WebRTC transports. The `bittorrent-protocol` npm package
reads from a Node.js stream interface that works identically over both transports.

### 4.3 Binary Data Handling

WebRTC data channels support both text and binary messages. WebTorrent uses
**binary mode exclusively** for the wire protocol.

**Browser (JavaScript):**
```javascript
// Data channel is configured for arraybuffer
dc.binaryType = 'arraybuffer';

// Messages arrive as ArrayBuffer
dc.onmessage = (event) => {
  const data = new Uint8Array(event.data);
  // Feed to wire protocol parser
};
```

**Browser (C# via BlazorJS):**
```csharp
// Set binary type
dc.BinaryType = "arraybuffer";

// Messages arrive as MessageEvent
dc.OnMessage += (MessageEvent e) => {
    using var ab = e.GetData<ArrayBuffer>();
    using var uint8 = new Uint8Array(ab);
    byte[] bytes = uint8.ReadBytes();
    // Feed to wire protocol parser
};
```

**Desktop (SipSorcery):**
```csharp
dc.onmessage += (RTCDataChannel channel, DataChannelPayloadProtocols protocol, byte[] data) => {
    // data is already a byte array
    // Feed to wire protocol parser
};
```

### 4.4 Handshake

The BT handshake is the first message sent by BOTH peers after the data channel opens.
It is 68 bytes with a fixed format:

```
Offset  Size  Field           Value
0       1     pstrlen         19 (0x13)
1       19    pstr            "BitTorrent protocol" (ASCII)
20      8     reserved        Extension flags (see below)
28      20    info_hash       SHA-1 hash of torrent's info dict
48      20    peer_id         Sender's 20-byte peer ID
```

**Reserved bytes (extension flags):**

```
Byte 5, bit 4 (0x10): BEP 10 Extension Protocol
Byte 7, bit 0 (0x01): DHT (BEP 5)
Byte 7, bit 2 (0x04): Fast Extension (BEP 6)

Example: 0x00 0x00 0x00 0x00 0x00 0x10 0x00 0x05
         = Extension Protocol + DHT + Fast Extension
```

Both peers send their handshake simultaneously (no request/response ordering).
After both handshakes are exchanged, the connection is established and message
exchange begins.

### 4.5 Message Types

After the handshake, all messages follow the length-prefixed format.
These are identical to standard BitTorrent:

| Length | ID | Name | Payload |
|--------|----|------|---------|
| 0 | - | keep-alive | (none - just 4 zero bytes) |
| 1 | 0 | choke | (none) |
| 1 | 1 | unchoke | (none) |
| 1 | 2 | interested | (none) |
| 1 | 3 | not_interested | (none) |
| 5 | 4 | have | uint32 piece_index |
| 1+N | 5 | bitfield | N bytes, MSB first |
| 13 | 6 | request | uint32 index, uint32 begin, uint32 length |
| 9+N | 7 | piece | uint32 index, uint32 begin, N bytes data |
| 13 | 8 | cancel | uint32 index, uint32 begin, uint32 length |
| 3 | 9 | port | uint16 listen_port (DHT) |
| 1 | 14 | have_all | (none) - BEP 6 Fast Extension |
| 1 | 15 | have_none | (none) - BEP 6 Fast Extension |
| 2+N | 20 | extended | uint8 ext_id, N bytes payload - BEP 10 |

### 4.6 Differences from TCP Wire Protocol

There are **no wire protocol differences**. The exact same bytes are sent over
WebRTC data channels as would be sent over TCP. This is by design - it allows:

1. The same wire protocol code to handle both TCP and WebRTC connections
2. Hybrid peers to bridge between TCP peers and WebRTC peers transparently
3. Any BEP extension (BEP 9 metadata, BEP 10 extended protocol, BEP 11 PEX)
   to work identically over both transports

The only operational difference is that WebRTC data channels have a maximum
message size (typically 256 KB), while TCP has no such limit. In practice this
is irrelevant because BitTorrent block sizes are 16 KB and the largest common
message (a piece message) is 16 KB + 9 bytes of header.

### 4.7 Backpressure

WebRTC data channels have a `bufferedAmount` property that indicates how many
bytes are queued for sending. The JS `simple-peer` library implements backpressure:

```javascript
// JS simple-peer: MAX_BUFFERED_AMOUNT = 64 * 1024 (64 KB)
// Wait before sending more data if buffer is full
while (dc.bufferedAmount > MAX_BUFFERED_AMOUNT) {
    await delay(10);
}
dc.send(data);
```

This prevents overwhelming the data channel and causing message drops.

---

## 5. Implementation Pitfalls

### 5.1 Binary String Encoding Bugs

**Symptom:** Client connects to tracker but never finds peers.

**Cause:** `info_hash` is being sent as hex string instead of binary string,
or the JSON serializer is escaping C1 control characters.

**Fix:** Verify that `info_hash` in the JSON is exactly 20 characters (not 40 hex chars).
Each character's code point should match the corresponding byte value.

### 5.2 Answer Message Contains Extra Fields

**Symptom:** Tracker receives answer but does not relay it.

**Cause:** The answer message includes `uploaded`, `downloaded`, `left`, or `offers`
fields. Some trackers treat this as a regular announce instead of an answer relay.

**Fix:** Answer messages should contain ONLY: `action`, `info_hash`, `peer_id`,
`to_peer_id`, `answer`, `offer_id`, and optionally `trackerid`.

### 5.3 Trickle ICE Not Filtered

**Symptom:** WebRTC connection fails with some peers.

**Cause:** SDP still contains `a=ice-options:trickle` line. Some implementations
interpret this as "more candidates will follow" and wait indefinitely.

**Fix:** Remove `a=ice-options:trickle` from the SDP before sending. The
`a=end-of-candidates` line should be present.

### 5.4 SipSorcery createAnswer Called Multiple Times

**Symptom:** Answer SDP is empty or malformed on desktop.

**Cause:** SipSorcery's `createAnswer()` returns null on second call.

**Fix:** Call `createAnswer()` exactly ONCE, set local description, wait for ICE,
then read the final SDP from `localDescription`.

### 5.5 Data Channel Not Created Before Offer

**Symptom:** Offer SDP does not contain `m=application` line.

**Cause:** The data channel must be created BEFORE `createOffer()` is called.
The data channel triggers the inclusion of the application media section in the SDP.

**Fix:** For the initiator: `createDataChannel()` first, then `createOffer()`.
For the responder: do NOT create a data channel; wait for `ondatachannel`.

### 5.6 offer_id Matching Failure

**Symptom:** Answers are received but no peer connection is established.

**Cause:** The `offer_id` in the answer relay is a binary string. Your client
is looking it up in a map keyed by hex strings (or vice versa).

**Fix:** Convert the binary string `offer_id` to hex before looking it up in
your pending offers map:

```csharp
var offerIdBinary = answerData.GetProperty("offer_id").GetString();
var offerIdHex = BinaryStringToHex(offerIdBinary);
// Now look up in _pendingOffers[offerIdHex]
```

### 5.7 Offers Sent with "completed" or "stopped" Events

**Symptom:** Tracker returns errors or peers receive offers from seeders
that have no intention of connecting.

**Cause:** Offers should only be sent with `"started"` events or regular
re-announces (no event field). Sending offers with `"completed"` wastes
resources generating WebRTC connections that will never be used.

### 5.8 Max Message Size Not Respected

**Symptom:** Large messages silently dropped by remote peer.

**Cause:** Sending data channel messages larger than `a=max-message-size`.

**Fix:** Check the remote SDP for `a=max-message-size`. If absent, assume 64 KB.
In practice, BitTorrent messages are well under this limit.

---

## 6. Complete Session Walkthrough

This walkthrough traces a complete WebTorrent session from announce to piece
download, using data captured from a live session.

### Setup

- Tracker: `ws://127.0.0.1:18900`
- Seeder peer ID: `-WW0208-2wEuB4yp+ScX`
- Downloader peer ID: `-WW0208-Siaz7FjHkr2D`
- File: `protocol-capture.bin` (49152 bytes, 3 pieces x 16384 bytes)
- Info hash: `863e15ae3ac365c56bfbd1139401ece3a55f8422`

### Phase 1: Seeder Joins Swarm

**[+0ms]** Seeder creates WebTorrent client, connects to tracker via WebSocket.

**[+34ms]** Seeder sends announce (completed - already has all data):

```json
{
  "action": "announce",
  "info_hash": "<binary string of 863e15ae...>",
  "peer_id": "-WW0208-2wEuB4yp+ScX",
  "uploaded": 0,
  "downloaded": 49152,
  "left": 0,
  "event": "completed",
  "numwant": 50
}
```

**[+35ms]** Tracker responds with swarm stats:

```json
{
  "action": "announce",
  "info_hash": "<binary string>",
  "interval": 120,
  "complete": 1,
  "incomplete": 0
}
```

### Phase 2: Downloader Joins and Gets Connected

**[+3570ms]** Downloader joins via magnet link. The tracker already has the seeder
registered. The downloader's `completed` announce triggers the seeder to re-announce
with offers.

**[+5034ms]** Seeder sends announce with 5 WebRTC offers:

```json
{
  "action": "announce",
  "info_hash": "<binary string>",
  "peer_id": "-WW0208-2wEuB4yp+ScX",
  "event": "started",
  "numwant": 5,
  "offers": [
    {
      "offer": { "type": "offer", "sdp": "v=0\r\no=rtc 3374675295 ..." },
      "offer_id": "<binary string of 26bf8f35...>"
    },
    { "...4 more offers..." }
  ]
}
```

**[+5035ms]** Tracker relays the first offer to the downloader:

```json
{
  "action": "announce",
  "info_hash": "<binary string>",
  "peer_id": "-WW0208-2wEuB4yp+ScX",
  "offer": { "type": "offer", "sdp": "v=0\r\no=rtc 3374675295 ..." },
  "offer_id": "<binary string of 26bf8f35...>"
}
```

The downloader creates an `RTCPeerConnection`, sets the remote description
to the offer SDP, creates an answer, waits for ICE gathering, and sends
the answer back through the tracker.

### Phase 3: Data Channel Opens, Wire Protocol Begins

**[+3585ms]** WebRTC data channel opens. Both peers immediately send the
BT handshake (68 bytes each).

### Phase 4: Extension Negotiation (BEP 10)

**[+3586ms]** Both peers send extended handshake (message ID 20, ext_id 0):

Seeder's handshake:
```
{ "m": { "lt_donthave": 3, "ut_metadata": 1, "ut_pex": 2 }, "metadata_size": 139 }
```

Downloader's handshake:
```
{ "m": { "lt_donthave": 3, "ut_metadata": 1, "ut_pex": 2 } }
```

### Phase 5: Metadata Exchange (BEP 9)

**[+3588ms]** Downloader requests metadata piece 0:
```
Bencoded: d8:msg_typei0e5:piecei0ee
```

**[+3588ms]** Seeder responds with metadata (139 bytes, fits in one piece):
```
Bencoded: d8:msg_typei1e5:piecei0e10:total_sizei139ee + [139 bytes raw info dict]
```

**[+3589ms]** Downloader verifies info dict SHA-1 hash matches the info_hash.
Now the downloader knows the file structure, piece hashes, and piece count.

### Phase 6: Piece Exchange

**[+3586ms]** Seeder sends `have_all` (BEP 6 Fast Extension, message ID 14).

Downloader sends `interested` (message ID 2).
Seeder sends `unchoke` (message ID 1).

Downloader sends `request` messages for all blocks:
```
request(index=0, begin=0, length=16384)
request(index=1, begin=0, length=16384)
request(index=2, begin=0, length=16384)
```

Seeder responds with `piece` messages containing the data.

**[+3590ms]** Download complete. Downloader verifies all piece hashes.
Sends `completed` announce to tracker.

### Timeline Summary

```
  0ms  Seeder created
 14ms  WebSocket connected to tracker
 34ms  Seeder announces "completed"
 35ms  Tracker responds: 1 seeder, 0 leechers
3570ms Downloader created, joins via magnet
5034ms Seeder re-announces with 5 offers
5035ms Tracker relays offer to downloader
       (WebRTC handshake happens here)
3585ms Data channel opens
3586ms BT handshake + have_all + extended handshake
3588ms Metadata exchange (ut_metadata)
3589ms Metadata verified
3590ms Piece exchange + download complete
3590ms Downloader announces "completed"
```

Total time from downloader creation to download complete: ~20ms of wire protocol
activity (the rest is WebRTC connection setup).

---

## 7. References

### Source Code

- [bittorrent-tracker](https://github.com/webtorrent/bittorrent-tracker) - JS reference tracker implementation
- [simple-peer](https://github.com/feross/simple-peer) - JS WebRTC abstraction used by WebTorrent
- [bittorrent-protocol](https://github.com/webtorrent/bittorrent-protocol) - JS wire protocol implementation
- [webtorrent](https://github.com/webtorrent/webtorrent) - JS WebTorrent client
- [wt-tracker](https://github.com/Novage/wt-tracker) - High-performance WebTorrent tracker (TypeScript)

### Documentation

- [WebTorrent FAQ](https://webtorrent.io/faq) - Official FAQ with protocol overview
- [WebTorrent API Docs](https://webtorrent.io/docs) - Client API documentation
- [libtorrent WebTorrent support](https://feross.org/libtorrent-webtorrent/) - Cross-client interop blog post

### Issues and Discussions

- [Websocket tracker protocol specification - Issue #257](https://github.com/webtorrent/bittorrent-tracker/issues/257) - Discussion about formalizing the protocol
- [Use Hex encoding for infohashes - Issue #1676](https://github.com/webtorrent/webtorrent/issues/1676) - Proposal to switch from binary strings to hex
- [offer_id validation - Issue #525](https://github.com/webtorrent/bittorrent-tracker/issues/525) - Tracker validation of offer_id size

### RFCs and Standards

- [RFC 4566](https://www.rfc-editor.org/rfc/rfc4566) - SDP: Session Description Protocol
- [RFC 8445](https://www.rfc-editor.org/rfc/rfc8445) - ICE: Interactive Connectivity Establishment
- [RFC 8841](https://www.rfc-editor.org/rfc/rfc8841) - SDP Offer/Answer for DTLS/SCTP Data Channels
- [BEP 3](https://www.bittorrent.org/beps/bep_0003.html) - The BitTorrent Protocol
- [BEP 6](https://www.bittorrent.org/beps/bep_0006.html) - Fast Extension
- [BEP 9](https://www.bittorrent.org/beps/bep_0009.html) - Extension for Peers to Send Metadata Files
- [BEP 10](https://www.bittorrent.org/beps/bep_0010.html) - Extension Protocol
