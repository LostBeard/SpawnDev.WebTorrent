# Tracker WebSocket Protocol

## Overview

The WebTorrent tracker uses WebSocket for peer discovery and WebRTC signaling relay.
All messages are JSON. Connection is initiated by the client to the tracker's announce URL.

## Binary String Encoding

Binary fields (`info_hash`, `offer_id`) use "binary string" encoding (latin1):
each byte value becomes a character with that char code. This matches JS `hex2bin()`.

`peer_id` is typically ASCII (e.g., `-WW0208-xxxxxxxxxxxx`) and needs no special encoding.

`info_hash` is 20 raw bytes that often include non-ASCII values,
so it MUST be binary-string encoded in the JSON.

## JSON Serialization

JS WebTorrent uses `JSON.stringify()` directly. The binary strings are written as
UTF-8 characters. For C# implementations, use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
and fix C1 control chars (0x80-0x9F) that System.Text.Json escapes as \u00XX.

## Message Types

### 1. Announce (client -> tracker)

Sent when joining/leaving a swarm.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `action` | string | yes | Always `"announce"` |
| `info_hash` | binary string | yes | 20-byte torrent info hash |
| `peer_id` | string | yes | 20-char peer ID (e.g., `-WW0208-xxxx`) |
| `uploaded` | number | yes | Bytes uploaded |
| `downloaded` | number | yes | Bytes downloaded |
| `left` | number | conditional | Bytes remaining. Omit or null for unknown |
| `event` | string | conditional | `"started"`, `"completed"`, or `"stopped"` |
| `numwant` | number | yes | Number of peers wanted |
| `offers` | array | conditional | WebRTC offers (only with `"started"`/update) |
| `trackerid` | string | conditional | Tracker-assigned ID (echo back if provided) |

**IMPORTANT:** Do NOT include `offers` with `"stopped"` or `"completed"` events.
Do NOT include extra fields the tracker doesn't expect - some trackers silently fail.

### 2. Announce Response (tracker -> client)

| Field | Type | Description |
|-------|------|-------------|
| `action` | string | `"announce"` |
| `info_hash` | binary string | Echo of the info hash |
| `interval` | number | Re-announce interval in seconds |
| `complete` | number | Number of seeders |
| `incomplete` | number | Number of leechers |

### 3. Offer Relay (tracker -> client)

Sent when another peer has an offer for this client.

| Field | Type | Description |
|-------|------|-------------|
| `action` | string | `"announce"` |
| `info_hash` | binary string | Info hash |
| `peer_id` | string | Offering peer's ID |
| `offer` | object | `{type: "offer", sdp: "..."}` |
| `offer_id` | binary string | Unique offer identifier |

### 4. Answer (client -> tracker)

Sent in response to a received offer.

| Field | Type | Description |
|-------|------|-------------|
| `action` | string | `"announce"` |
| `info_hash` | binary string | Info hash |
| `peer_id` | string | This client's peer ID |
| `to_peer_id` | string | Target peer's ID (from the offer) |
| `answer` | object | `{type: "answer", sdp: "..."}` |
| `offer_id` | binary string | Must match the offer's offer_id |
| `trackerid` | string | Optional, include if tracker provided one |

**NOTE:** The answer message MUST NOT include `uploaded`, `downloaded`, `left`,
`event`, `numwant`, or `offers`. Only the fields listed above.

### 5. Answer Relay (tracker -> client)

Sent to the original offerer with the answering peer's response.

| Field | Type | Description |
|-------|------|-------------|
| `action` | string | `"announce"` |
| `info_hash` | binary string | Info hash |
| `peer_id` | string | Answering peer's ID |
| `answer` | object | `{type: "answer", sdp: "..."}` |
| `offer_id` | binary string | Matching offer_id |

### Offer Object Format

Each offer in the `offers` array:

```json
{
  "offer": {
    "type": "offer",
    "sdp": "v=0\r\no=..."
  },
  "offer_id": "<20 bytes binary string>"
}
```

## Captured Messages

### [+14ms] client_connected

```json
{
  "event": "client_connected",
  "addr": "::ffff:127.0.0.1:60598"
}
```

### [+34ms] message_received

```json
{
  "event": "message_received",
  "direction": "client->tracker",
  "addr": "::ffff:127.0.0.1:60598",
  "action": "announce",
  "keys": [
    "numwant",
    "uploaded",
    "downloaded",
    "left",
    "event",
    "action",
    "info_hash",
    "peer_id"
  ],
  "info_hash_hex": "863e15ae3ac365c56bfbd1139401ece3a55f8422",
  "peer_id_raw": "-WW0208-2wEuB4yp+ScX",
  "peer_id_hex": "2d5757303230382d32774575423479702b536358",
  "event_type": "completed",
  "numwant": 50,
  "uploaded": 0,
  "downloaded": 49152,
  "left": 0
}
```

### [+35ms] message_sent

```json
{
  "event": "message_sent",
  "direction": "tracker->client",
  "addr": "::ffff:127.0.0.1:60598",
  "action": "announce",
  "keys": [
    "complete",
    "incomplete",
    "action",
    "interval",
    "info_hash"
  ],
  "info_hash_hex": "863e15ae3ac365c56bfbd1139401ece3a55f8422",
  "interval": 120,
  "complete": 1,
  "incomplete": 0
}
```

### [+3590ms] message_received

```json
{
  "event": "message_received",
  "direction": "client->tracker",
  "addr": "::ffff:127.0.0.1:60598",
  "action": "announce",
  "keys": [
    "numwant",
    "uploaded",
    "downloaded",
    "left",
    "event",
    "action",
    "info_hash",
    "peer_id"
  ],
  "info_hash_hex": "863e15ae3ac365c56bfbd1139401ece3a55f8422",
  "peer_id_raw": "-WW0208-Siaz7FjHkr2D",
  "peer_id_hex": "2d5757303230382d5369617a37466a486b723244",
  "event_type": "completed",
  "numwant": 50,
  "uploaded": 157,
  "downloaded": 49152,
  "left": 0
}
```

### [+3590ms] message_sent

```json
{
  "event": "message_sent",
  "direction": "tracker->client",
  "addr": "::ffff:127.0.0.1:60598",
  "action": "announce",
  "keys": [
    "complete",
    "incomplete",
    "action",
    "interval",
    "info_hash"
  ],
  "info_hash_hex": "863e15ae3ac365c56bfbd1139401ece3a55f8422",
  "interval": 120,
  "complete": 2,
  "incomplete": 0
}
```

### [+5034ms] message_received

```json
{
  "event": "message_received",
  "direction": "client->tracker",
  "addr": "::ffff:127.0.0.1:60598",
  "action": "announce",
  "keys": [
    "numwant",
    "uploaded",
    "downloaded",
    "left",
    "event",
    "action",
    "info_hash",
    "peer_id",
    "offers"
  ],
  "info_hash_hex": "863e15ae3ac365c56bfbd1139401ece3a55f8422",
  "peer_id_raw": "-WW0208-2wEuB4yp+ScX",
  "peer_id_hex": "2d5757303230382d32774575423479702b536358",
  "event_type": "started",
  "numwant": 5,
  "uploaded": 0,
  "downloaded": 49152,
  "left": 0,
  "offers_count": 5,
  "offers": [
    {
      "index": 0,
      "offer_id_hex": "26bf8f3585e2be678f0da2a3fd4a65e1cc264ccf",
      "sdp_type": "offer",
      "sdp": "v=0\r\no=rtc 3374675295 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=msid-semantic:WMS *\r\na=ice-options:ice2,trickle\r\na=fingerprint:sha-256 57:90:89:53:7A:DE:03:06:0C:C0:2D:21:C7:2A:03:73:CD:37:F0:6C:2C:8C:81:7E:53:FF:FB:FB:60:38:04:2B\r\nm=application 55204 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 192.168.1.120\r\na=mid:0\r\na=sendrecv\r\na=sctp-port:5000\r\na=max-message-size:262144\r\na=setup:actpass\r\na=ice-ufrag:TC8V\r\na=ice-pwd:JpeiYW9htg3PQxpDvxZSc0\r\na=candidate:1 1 UDP 2114977791 192.168.1.120 55204 typ host\r\na=candidate:2 1 UDP 1678769919 64.246.234.108 55204 typ srflx raddr 0.0.0.0 rport 0\r\na=end-of-candidates\r\n"
    },
    {
      "index": 1,
      "offer_id_hex": "ff5233428a90a9720d68b5fe8d54b3c3c8a9b757",
      "sdp_type": "offer",
      "sdp": "v=0\r\no=rtc 2610308160 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=msid-semantic:WMS *\r\na=ice-options:ice2,trickle\r\na=fingerprint:sha-256 A9:D2:39:F2:A2:3E:C1:84:E6:6F:E0:BB:C5:BF:D2:3B:91:56:B8:29:F8:8F:2A:E6:1A:BC:6D:E8:5E:9D:EF:49\r\nm=application 55205 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 192.168.1.120\r\na=mid:0\r\na=sendrecv\r\na=sctp-port:5000\r\na=max-message-size:262144\r\na=setup:actpass\r\na=ice-ufrag:s0x6\r\na=ice-pwd:QdW5m9EkZ1KTixl9oOPG7Z\r\na=candidate:1 1 UDP 2114977791 192.168.1.120 55205 typ host\r\na=candidate:2 1 UDP 1678769919 64.246.234.108 55205 typ srflx raddr 0.0.0.0 rport 0\r\na=end-of-candidates\r\n"
    },
    {
      "index": 2,
      "offer_id_hex": "5d5482331b7b2551268ca7bb37298eda4b23c03d",
      "sdp_type": "offer",
      "sdp": "v=0\r\no=rtc 1303727296 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=msid-semantic:WMS *\r\na=ice-options:ice2,trickle\r\na=fingerprint:sha-256 F5:9C:7A:7C:B8:EF:25:AA:D7:F3:80:37:D7:3A:EC:99:F0:ED:D4:36:B9:40:74:3B:6B:43:75:D6:5D:DF:05:BF\r\nm=application 55206 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 192.168.1.120\r\na=mid:0\r\na=sendrecv\r\na=sctp-port:5000\r\na=max-message-size:262144\r\na=setup:actpass\r\na=ice-ufrag:H2yS\r\na=ice-pwd:Wk9o8cvPBw/VrdgtFTIoBq\r\na=candidate:1 1 UDP 2114977791 192.168.1.120 55206 typ host\r\na=candidate:2 1 UDP 1678769919 64.246.234.108 55206 typ srflx raddr 0.0.0.0 rport 0\r\na=end-of-candidates\r\n"
    },
    {
      "index": 3,
      "offer_id_hex": "35e57462e118ee04583e7a97993216f94aba87b8",
      "sdp_type": "offer",
      "sdp": "v=0\r\no=rtc 4099561181 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=msid-semantic:WMS *\r\na=ice-options:ice2,trickle\r\na=fingerprint:sha-256 AC:03:99:18:27:5C:D0:AB:A8:8C:F9:16:EA:B9:02:A9:7B:DB:F9:F8:5C:F5:B5:7D:47:58:1E:AC:FC:53:00:78\r\nm=application 55207 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 192.168.1.120\r\na=mid:0\r\na=sendrecv\r\na=sctp-port:5000\r\na=max-message-size:262144\r\na=setup:actpass\r\na=ice-ufrag:CJiU\r\na=ice-pwd:gAVN1xRfLGAIVNiA4Nlu/2\r\na=candidate:1 1 UDP 2114977791 192.168.1.120 55207 typ host\r\na=candidate:2 1 UDP 1678769919 64.246.234.108 55207 typ srflx raddr 0.0.0.0 rport 0\r\na=end-of-candidates\r\n"
    },
    {
      "index": 4,
      "offer_id_hex": "a291562f866cd2a564ac178692a27d7fc2f46c89",
      "sdp_type": "offer",
      "sdp": "v=0\r\no=rtc 567055318 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=msid-semantic:WMS *\r\na=ice-options:ice2,trickle\r\na=fingerprint:sha-256 28:32:3B:CB:EC:9C:22:F6:0C:1D:1B:D2:C2:8B:8B:EA:AD:0E:51:8F:F1:65:F0:D7:EB:FA:1D:73:EB:09:3F:BB\r\nm=application 55208 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 192.168.1.120\r\na=mid:0\r\na=sendrecv\r\na=sctp-port:5000\r\na=max-message-size:262144\r\na=setup:actpass\r\na=ice-ufrag:Y9d4\r\na=ice-pwd:9OIfMt5eqSLrc5LIPDEbkn\r\na=candidate:1 1 UDP 2114977791 192.168.1.120 55208 typ host\r\na=candidate:2 1 UDP 1678769919 64.246.234.108 55208 typ srflx raddr 0.0.0.0 rport 0\r\na=end-of-candidates\r\n"
    }
  ]
}
```

### [+5034ms] message_sent

```json
{
  "event": "message_sent",
  "direction": "tracker->client",
  "addr": "::ffff:127.0.0.1:60598",
  "action": "announce",
  "keys": [
    "complete",
    "incomplete",
    "action",
    "interval",
    "info_hash"
  ],
  "info_hash_hex": "863e15ae3ac365c56bfbd1139401ece3a55f8422",
  "interval": 120,
  "complete": 2,
  "incomplete": 0
}
```

### [+5035ms] message_sent

```json
{
  "event": "message_sent",
  "direction": "tracker->client",
  "addr": "::ffff:127.0.0.1:60598",
  "action": "announce",
  "keys": [
    "action",
    "offer",
    "offer_id",
    "peer_id",
    "info_hash"
  ],
  "info_hash_hex": "863e15ae3ac365c56bfbd1139401ece3a55f8422",
  "peer_id_hex": "2d5757303230382d32774575423479702b536358",
  "offer_id_hex": "26bf8f3585e2be678f0da2a3fd4a65e1cc264ccf",
  "offer": {
    "type": "offer",
    "sdp": "v=0\r\no=rtc 3374675295 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=msid-semantic:WMS *\r\na=ice-options:ice2,trickle\r\na=fingerprint:sha-256 57:90:89:53:7A:DE:03:06:0C:C0:2D:21:C7:2A:03:73:CD:37:F0:6C:2C:8C:81:7E:53:FF:FB:FB:60:38:04:2B\r\nm=application 55204 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 192.168.1.120\r\na=mid:0\r\na=sendrecv\r\na=sctp-port:5000\r\na=max-message-size:262144\r\na=setup:actpass\r\na=ice-ufrag:TC8V\r\na=ice-pwd:JpeiYW9htg3PQxpDvxZSc0\r\na=candidate:1 1 UDP 2114977791 192.168.1.120 55204 typ host\r\na=candidate:2 1 UDP 1678769919 64.246.234.108 55204 typ srflx raddr 0.0.0.0 rport 0\r\na=end-of-candidates\r\n"
  }
}
```

