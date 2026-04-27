# BitTorrent Tracker Protocols

Comprehensive reference for the three tracker protocol families used in BitTorrent: HTTP (BEP 3 / BEP 48), UDP (BEP 15 / BEP 41), and WebSocket (WebTorrent). This document covers every message format, byte-level details, and protocol semantics.

---

## Table of Contents

1. [HTTP Tracker Protocol](#http-tracker-protocol)
   - [Announce Request](#announce-request)
   - [Announce Response](#announce-response)
   - [Event Semantics](#event-semantics)
   - [Compact Peer Format (BEP 23)](#compact-peer-format-bep-23)
   - [Scrape Protocol (BEP 48)](#scrape-protocol-bep-48)
   - [tracker_id Handling](#tracker_id-handling)
2. [UDP Tracker Protocol (BEP 15)](#udp-tracker-protocol-bep-15)
   - [Connection Handshake](#connection-handshake)
   - [Announce](#udp-announce)
   - [Scrape](#udp-scrape)
   - [Error Response](#udp-error-response)
   - [Timeout and Retry Algorithm](#timeout-and-retry-algorithm)
   - [Extensions (BEP 41)](#extensions-bep-41)
3. [WebSocket Tracker Protocol (WebTorrent)](#websocket-tracker-protocol-webtorrent)
   - [Connection Management](#ws-connection-management)
   - [Announce with Offers](#ws-announce-with-offers)
   - [Offer Relay](#ws-offer-relay)
   - [Answer Relay](#ws-answer-relay)
   - [Scrape](#ws-scrape)
   - [Binary String Encoding](#ws-binary-string-encoding)
   - [Comparison with HTTP/UDP Semantics](#comparison-with-httpudp-semantics)
4. [Implementation Notes for SpawnDev.WebTorrent](#implementation-notes-for-spawndevwebtorrent)

---

## HTTP Tracker Protocol

**Specifications:** BEP 3, BEP 23, BEP 48  
**Transport:** HTTP GET requests, bencoded responses

The HTTP tracker is the original BitTorrent tracker protocol. Clients periodically send HTTP GET requests to the tracker's announce URL, and the tracker responds with peer lists and swarm statistics.

### Announce Request

Clients send a GET request to the tracker's announce URL with the following query parameters:

#### Required Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `info_hash` | 20 bytes (URL-encoded) | SHA-1 hash of the bencoded info dictionary. Must be URL-encoded (any byte not in `0-9, a-z, A-Z, '.', '-', '_', '~'` becomes `%XX`). |
| `peer_id` | 20 bytes (URL-encoded) | Unique identifier for this client. Must be URL-encoded using the same rules as info_hash. |
| `port` | Integer | Port number this peer is listening on. Typically in the range 6881-6889. |
| `uploaded` | Integer (ASCII) | Total number of bytes uploaded since the "started" event, encoded in base-10 ASCII. |
| `downloaded` | Integer (ASCII) | Total number of bytes downloaded since the "started" event, encoded in base-10 ASCII. |
| `left` | Integer (ASCII) | Number of bytes this peer still needs to download. Set to `0` when seeding. |

#### Optional Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `compact` | `0` or `1` | `1` = accept compact peer format (BEP 23). `0` = prefer dictionary format. This is **advisory only** - the tracker may return either format regardless. |
| `no_peer_id` | (flag) | If present, tracker may omit the `peer id` field from non-compact responses. Ignored when `compact=1`. |
| `event` | String | One of: `started`, `stopped`, `completed`, or empty/omitted for regular announces. See [Event Semantics](#event-semantics). |
| `ip` | String | Dotted-quad IPv4 address or DNS name of this peer. Used when the client's request IP differs from its actual IP (NAT, proxy). Generally only honored from trusted clients. |
| `numwant` | Integer | Number of peers the client wants in the response. Default is **50**. Tracker is not required to honor this. |
| `key` | String | An opaque identifier not shared with other peers. Allows a client to prove identity if its IP address changes (e.g., NAT rebinding). |
| `trackerid` | String | If a previous announce response included a `tracker id`, include it here. See [tracker_id Handling](#tracker_id-handling). |

#### Example Request

```
GET /announce?info_hash=%124Vx%9A%BC%DE%F1%23%45%67%89%AB%CD%EF%01%23%45%67%89
    &peer_id=-SD0300-123456789012
    &port=6881
    &uploaded=0
    &downloaded=0
    &left=1048576
    &compact=1
    &event=started
    &numwant=50 HTTP/1.1
Host: tracker.example.com
```

### Announce Response

The tracker responds with a bencoded dictionary.

#### Failure Response

If the request is invalid or the tracker encounters an error:

```
d14:failure reason24:Torrent not registerede
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `failure reason` | String | Yes (on error) | Human-readable error message. If present, no other keys may be present. |

#### Success Response

```
d
  8:completei15e
  10:incompletei47e
  8:intervali1800e
  12:min intervali900e
  10:tracker id8:abcd1234
  5:peers300:<compact peer data>
e
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `interval` | Integer | Yes | Number of seconds the client should wait before the next regular announce. |
| `min interval` | Integer | No | Minimum announce interval. If present, clients must NOT re-announce more frequently. |
| `tracker id` | String | No | An opaque string the client should include as `trackerid` in future announces. |
| `complete` | Integer | No | Number of seeders (peers with complete file). |
| `incomplete` | Integer | No | Number of leechers (peers still downloading). |
| `peers` | List or String | Yes | Peer list - either dictionary format or compact format (BEP 23). |
| `warning message` | String | No | Human-readable warning. Unlike `failure reason`, the response is still processed normally. |

#### Peers - Dictionary Format

When `compact` is not requested or the tracker does not support compact:

```
d
  5:peers
    l
      d
        7:peer id20:-SD0300-abcdefghijkl
        2:ip14:192.168.1.100
        4:porti6881e
      e
      d
        7:peer id20:-UT3500-123456789012
        2:ip11:10.0.0.5
        4:porti51413e
      e
    e
e
```

Each peer dictionary contains:

| Field | Type | Description |
|-------|------|-------------|
| `peer id` | 20-byte String | The peer's self-chosen ID. Omitted if `no_peer_id` was set in the request. |
| `ip` | String | IPv4 address, IPv6 address, or DNS hostname |
| `port` | Integer | Listening port |

#### Peers - Compact Format (BEP 23)

When compact format is used, `peers` is a binary string instead of a list. Each peer is 6 bytes:

```
+---+---+---+---+---+---+
| IP Address    | Port  |
| (4 bytes)     | (2B)  |
| network order | BE    |
+---+---+---+---+---+---+
```

**Total size:** `6 * num_peers` bytes

Example: 3 peers = 18 bytes of compact data.

Compact format does NOT include peer IDs - they are omitted entirely. This dramatically reduces response size:
- Dictionary format: ~289 bytes per peer
- Compact format: 6 bytes per peer
- Savings: ~98% reduction in response size

**IPv6 compact format:** When the tracker returns IPv6 peers, a separate `peers6` key is used. Each IPv6 peer is 18 bytes (16 bytes address + 2 bytes port).

### Event Semantics

| Event | When to Send | Notes |
|-------|-------------|-------|
| `started` | First announce to this tracker for this torrent | **Mandatory** for the first request. Signals a new peer joining the swarm. |
| `stopped` | Client is shutting down gracefully | Allows the tracker to immediately remove the peer from its lists. |
| `completed` | Download just completed | Must NOT be sent if the download was already 100% complete when the client started (i.e., the client was already a seed). Only sent once, at the moment of completion. |
| (empty/omitted) | Regular periodic announces | Sent at the `interval` period. No special event - just a keepalive. |

**Event lifecycle:**
```
started ---> (empty) ---> (empty) ---> completed ---> (empty) ---> stopped
   |            |            |             |              |           |
   v            v            v             v              v           v
 Join swarm  Keepalive   Keepalive    Finished DL     Keepalive   Leave swarm
```

### Compact Peer Format (BEP 23)

**Specification:** https://www.bittorrent.org/beps/bep_0023.html

BEP 23 defines the compact peer list format. Key points:

1. **Client signals preference:** `compact=1` in the announce request
2. **Tracker decides:** The `compact` parameter is **advisory only** - the tracker may return either format
3. **Clients must support both:** Even if a client sends `compact=1`, it must handle dictionary responses
4. **Peer ID is omitted:** Compact format does not include peer IDs. This is acceptable because peer IDs are not needed for establishing connections

**Byte layout (IPv4):**
```
Offset  Size  Field
0       4     IP address (network byte order / big-endian)
4       2     Port number (big-endian)
```

**Parsing example (C#):**
```csharp
// Parse compact peer list
for (int i = 0; i < data.Length; i += 6)
{
    var ip = new IPAddress(data[i..(i + 4)]);
    var port = (data[i + 4] << 8) | data[i + 5];
    // ip:port is one peer
}
```

### Scrape Protocol (BEP 48)

**Specification:** https://www.bittorrent.org/beps/bep_0048.html

The scrape protocol lets clients query swarm statistics without joining the swarm.

#### URL Construction

Derive the scrape URL from the announce URL by replacing the last occurrence of `announce` in the path with `scrape`:

```
Announce: http://tracker.example.com/announce
Scrape:   http://tracker.example.com/scrape

Announce: http://tracker.example.com/x/announce
Scrape:   http://tracker.example.com/x/scrape

Announce: http://tracker.example.com/announce.php
Scrape:   http://tracker.example.com/scrape.php
```

If the announce URL does not contain the string `announce` in its path, scrape is not supported for that tracker.

#### Scrape Request

A simple GET request with optional `info_hash` parameters:

```
GET /scrape?info_hash=%12%34...&info_hash=%56%78... HTTP/1.1
Host: tracker.example.com
```

- **No `info_hash` parameter:** Tracker returns stats for ALL torrents it knows about
- **One `info_hash`:** Stats for that specific torrent
- **Multiple `info_hash`:** Stats for each specified torrent (parameter appears multiple times)

#### Scrape Response

A bencoded dictionary:

```
d
  5:files
    d
      20:<info_hash_bytes_1>
        d
          8:completei15e
          10:downloadedi1234e
          10:incompletei47e
          4:name14:Example Torrent
        e
      20:<info_hash_bytes_2>
        d
          8:completei3e
          10:downloadedi56e
          10:incompletei12e
        e
    e
e
```

| Field | Type | Description |
|-------|------|-------------|
| `files` | Dictionary | Keyed by 20-byte raw info hash (not URL-encoded). Each value is a dictionary. |

**Per-torrent fields:**

| Field | Type | Description |
|-------|------|-------------|
| `complete` | Integer | Number of active peers that have the complete file (seeders) |
| `downloaded` | Integer | Total number of times the file has been completely downloaded (all-time) |
| `incomplete` | Integer | Number of active peers that do not have the complete file (leechers) |
| `name` | String | (Optional) Torrent name |

**Failure response:**
```
d14:failure reason20:Scrape not allowede
```

### tracker_id Handling

The `tracker_id` mechanism provides session continuity between a client and tracker:

1. **Tracker sends `tracker id`:** The announce response may contain a `tracker id` string
2. **Client stores it:** The client saves this value
3. **Client echoes it back:** On subsequent announces, the client includes `trackerid=<saved_value>`
4. **Persistence rule:** If a subsequent response does NOT contain a `tracker id`, the client keeps using the previously stored value. Only discard it if the tracker sends a new one.

This allows trackers to maintain state about a client across multiple announce cycles, even if the client's IP address changes.

---

## UDP Tracker Protocol (BEP 15)

**Specification:** https://www.bittorrent.org/beps/bep_0015.html  
**Transport:** UDP datagrams, binary format, network byte order (big-endian)

The UDP tracker protocol reduces overhead compared to HTTP by using compact binary messages over UDP. It requires a connection handshake to prevent source address spoofing.

### Connection Handshake

Before any announce or scrape, the client must obtain a connection ID from the tracker.

#### Connect Request (16 bytes)

```
Offset  Size    Field           Value
0       8       protocol_id     0x0000041727101980 (magic constant)
8       4       action          0 (connect)
12      4       transaction_id  Random 32-bit integer
```

**Byte layout:**
```
+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
|        protocol_id         |  action  | trans_id |
| 00 00 04 17 27 10 19 80   | 00 00 00 | xx xx xx |
|                            | 00       | xx       |
+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
```

The **magic constant** `0x41727101980` is the protocol identifier. It serves as a simple validation that this is a BitTorrent UDP tracker packet and not random traffic.

#### Connect Response (16 bytes)

```
Offset  Size    Field           Value
0       4       action          0 (connect)
4       4       transaction_id  Must match request
8       8       connection_id   Tracker-assigned ID
```

**Byte layout:**
```
+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
|  action  | trans_id |      connection_id         |
| 00 00 00 | xx xx xx | yy yy yy yy yy yy yy yy  |
| 00       | xx       |                            |
+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
```

#### Connection ID Lifecycle

- **Client:** May use a connection ID for up to **1 minute** after receipt
- **Tracker:** Should accept connection IDs for up to **2 minutes** after transmission
- **Purpose:** Prevents UDP source address spoofing by requiring a handshake
- **Reuse:** After expiration, the client must perform a new connect handshake

### UDP Announce

#### Announce Request (98 bytes minimum)

```
Offset  Size    Field           Description
0       8       connection_id   From connect response
8       4       action          1 (announce)
12      4       transaction_id  Random 32-bit integer
16      20      info_hash       SHA-1 hash of info dictionary
36      20      peer_id         Client's peer ID
56      8       downloaded      Total bytes downloaded (64-bit)
64      8       left            Bytes remaining (64-bit)
72      8       uploaded        Total bytes uploaded (64-bit)
80      4       event           See event table below
84      4       IP address      Client IP (0 = use source address)
88      4       key             Random key for identity (not shared with peers)
92      4       num_want        Desired number of peers (-1 = default)
96      2       port            Client listen port
```

**Event values (different from HTTP string values):**

| Value | Meaning |
|-------|---------|
| 0 | None (regular announce) |
| 1 | Completed |
| 2 | Started |
| 3 | Stopped |

**Note:** The event numbering differs from HTTP - `started` is 2 in UDP but just the string `"started"` in HTTP.

**IP address field:** Set to `0` to have the tracker use the source IP from the UDP packet. Only set a non-zero value if the client is behind a proxy and the tracker is configured to honor this field. For IPv6 announces, this field should be set to `0` (the field is only 4 bytes and cannot hold an IPv6 address).

#### Announce Response (20+ bytes)

```
Offset  Size    Field           Description
0       4       action          1 (announce)
4       4       transaction_id  Must match request
8       4       interval        Seconds until next announce
12      4       leechers        Number of peers downloading
16      4       seeders         Number of peers seeding
20+     6*N     peers           Compact peer list (IPv4)
```

**Peer format (IPv4) - 6 bytes each:**
```
Offset  Size    Field
0       4       IP address (network byte order)
4       2       Port (big-endian)
```

**Peer format (IPv6) - 18 bytes each:**
```
Offset  Size    Field
0       16      IPv6 address (network byte order)
16      2       Port (big-endian)
```

The protocol format (IPv4 vs IPv6) is determined by the underlying UDP packet's address family. If the announce was sent over IPv6, the peer list uses 18-byte entries. If over IPv4, the peer list uses 6-byte entries.

### UDP Scrape

#### Scrape Request (16+ bytes)

```
Offset  Size    Field           Description
0       8       connection_id   From connect response
8       4       action          2 (scrape)
12      4       transaction_id  Random 32-bit integer
16      20*N    info_hashes     One or more 20-byte info hashes
```

**Maximum hashes per request:** The UDP packet should stay within the MTU. With a typical MTU of ~1500 bytes, the practical limit is about 74 info hashes per scrape request: `(1500 - 16) / 20 = 74`

#### Scrape Response (8+ bytes)

```
Offset  Size    Field           Description
0       4       action          2 (scrape)
4       4       transaction_id  Must match request
8       12*N    torrent_stats   12 bytes per info hash, in request order
```

**Per-torrent stats (12 bytes each):**
```
Offset  Size    Field           Description
0       4       seeders         Number of seeders
4       4       completed       Total completed downloads (all-time)
8       4       leechers        Number of leechers
```

The stats appear in the same order as the info hashes in the request.

### UDP Error Response

```
Offset  Size    Field           Description
0       4       action          3 (error)
4       4       transaction_id  Must match request
8       N       message         Human-readable error string (UTF-8)
```

The error message is a variable-length string that extends to the end of the UDP packet.

### Timeout and Retry Algorithm

If a response is not received within the timeout period, the client retransmits the request. The timeout follows an exponential backoff:

```
timeout = 15 * 2^n seconds
```

Where `n` starts at 0 and increments after each retransmission, up to `n = 8`:

| Attempt | n | Timeout (seconds) | Cumulative Wait |
|---------|---|-------------------|-----------------|
| 1 | 0 | 15 | 15s |
| 2 | 1 | 30 | 45s |
| 3 | 2 | 60 | 1m 45s |
| 4 | 3 | 120 | 3m 45s |
| 5 | 4 | 240 | 7m 45s |
| 6 | 5 | 480 | 15m 45s |
| 7 | 6 | 960 | 31m 45s |
| 8 | 7 | 1920 | 63m 45s |
| 9 | 8 | 3840 | 127m 45s |

After 9 attempts (n=8) with no response, the client should give up on this tracker and try the next one in the announce list.

**Important:** The `transaction_id` should remain the same across retransmissions of the same request. Generate a new `transaction_id` only for new requests.

### Action Value Summary

| Value | Action |
|-------|--------|
| 0 | Connect |
| 1 | Announce |
| 2 | Scrape |
| 3 | Error |

### Extensions (BEP 41)

**Specification:** https://www.bittorrent.org/beps/bep_0041.html

BEP 41 defines a mechanism to append extension options to UDP announce requests.

#### Extension Format

Extensions are appended to the announce request **after byte 98** (immediately after the standard BEP 15 fields). Parsing continues until the packet ends or an EndOfOptions marker is encountered.

Each option consists of:
```
+----------+-----------+---------+
| Type (1B)| Len (1B)  | Data    |
+----------+-----------+---------+
```

**Critical parsing rule:** Option types `<= 0x01` are NEVER followed by a length byte. Option types `>= 0x02` are ALWAYS followed by a length byte.

#### Defined Option Types

| Type | Name | Length Field | Description |
|------|------|-------------|-------------|
| `0x00` | EndOfOptions | No | Signals end of options. Parsing stops. Optional - parsing also stops at packet end. |
| `0x01` | NOP | No | No operation. Padding byte with no functional effect. |
| `0x02` | URLData | Yes | Carries PATH and QUERY components from the tracker URL. |

**URLData option:** Allows embedding URL path and query string data in the UDP packet. Multiple URLData options concatenate their data fields, enabling path/query strings longer than 255 bytes.

```
Example URLData option:
+------+------+--------------------+
| 0x02 | 0x15 | /announce?auth=xyz |
+------+------+--------------------+
  type   len=21  data (21 bytes)
```

**Limitations:**
- Extensions can only be sent by the **client** (in announce requests)
- The tracker cannot include extensions in its responses
- Only the Announce Request packet supports extensions

### IPv6 Considerations

The UDP tracker protocol works over both IPv4 and IPv6:

- The **request format is identical** for both address families
- The **response peer list stride changes:** 6 bytes per peer for IPv4, 18 bytes per peer for IPv6
- The `IP address` field in the announce request (offset 84) should be set to `0` for IPv6, as it only holds 4 bytes
- The protocol family is determined by the socket's address family, not by anything in the packet itself

### Complete UDP Exchange Sequence

```
Client                                    Tracker
  |                                          |
  |--- Connect Request --------------------->|
  |    protocol_id = 0x41727101980           |
  |    action = 0                            |
  |    transaction_id = 0xAABBCCDD           |
  |                                          |
  |<-- Connect Response ---------------------|
  |    action = 0                            |
  |    transaction_id = 0xAABBCCDD           |
  |    connection_id = 0x1234567890ABCDEF    |
  |                                          |
  |--- Announce Request -------------------->|
  |    connection_id = 0x1234567890ABCDEF    |
  |    action = 1                            |
  |    transaction_id = 0x11223344           |
  |    info_hash = <20 bytes>                |
  |    peer_id = <20 bytes>                  |
  |    event = 2 (started)                   |
  |    ... (other fields)                    |
  |                                          |
  |<-- Announce Response --------------------|
  |    action = 1                            |
  |    transaction_id = 0x11223344           |
  |    interval = 1800                       |
  |    leechers = 47                         |
  |    seeders = 15                          |
  |    peers = [6 bytes * N]                 |
  |                                          |
  |    ... 1800 seconds later ...            |
  |                                          |
  |--- Announce Request -------------------->|
  |    (event = 0, regular announce)         |
  |                                          |
```

---

## WebSocket Tracker Protocol (WebTorrent)

**Status:** No formal BEP - defined by the WebTorrent reference implementation  
**Transport:** WebSocket (wss://) with JSON messages  
**Source:** https://github.com/webtorrent/bittorrent-tracker

### Client coverage

The WebSocket tracker is a WebTorrent-ecosystem extension. Mainline BitTorrent clients (libtorrent family) do NOT support it — verified 2026-04-27 against qBittorrent 5.1.4 / libtorrent 2.0.11 which returned status `4` "unsupported URL protocol" when given a `wss://` tracker URL.

| Client | WS tracker | WebRTC peer-wire | TCP/uTP peer-wire |
|---|---|---|---|
| Browser SpawnDev.WebTorrent | ✓ | ✓ | ✗ |
| Browser webtorrent.js / Brave Browser | ✓ | ✓ | ✗ |
| Node.js `webtorrent@^2` | ✓ | ✓ | ✗ |
| Node.js `webtorrent-hybrid` | ✓ | ✓ | ✓ (bridge) |
| WebTorrent Desktop | ✓ | ✓ | ✓ (bridge) |
| Desktop SpawnDev.WebTorrent | ✓ | ✓ | ✓ (bridge) |
| qBittorrent / libtorrent 2.0 | ✗ | ✗ | ✓ |
| Transmission / Deluge / rqbit | ✗ | ✗ | ✓ |

Browser-only WebTorrent peers and TCP-only mainline peers cannot reach each other directly — they need a "bridge" peer (Desktop SpawnDev.WebTorrent, `webtorrent-hybrid`, or WebTorrent Desktop) that speaks both transports.

The WebSocket tracker protocol was designed for WebTorrent to enable browser-based BitTorrent peers. Since browsers cannot use UDP or make raw TCP connections, the tracker serves a dual purpose: peer discovery AND WebRTC signaling relay.

### Overview

Unlike HTTP/UDP trackers that simply return peer lists, the WebSocket tracker actively relays WebRTC signaling data (SDP offers and answers) between peers. This is necessary because WebRTC requires a signaling channel to establish peer-to-peer connections, and the tracker provides that channel.

**Flow:**
1. Client A connects to tracker via WebSocket
2. Client A sends announce with SDP offers
3. Tracker relays each offer to a different peer in the swarm
4. Target peers create SDP answers and send them back through the tracker
5. Tracker relays answers to Client A
6. WebRTC connections are established directly between peers

### WS Connection Management

**URL format:** `wss://tracker.example.com/announce` or `wss://tracker.example.com`

**Connection lifecycle:**
- Client opens WebSocket connection to tracker URL
- Connection is persistent (not per-request like HTTP)
- Multiple torrents can share a single WebSocket connection
- Client announces periodically (default interval: typically 120 seconds, configurable by tracker)

**Reconnection:** On disconnect, clients use exponential backoff:
- Minimum: 10 seconds
- Maximum: 60 hours (in practice, much less)
- Each failed attempt doubles the backoff

**Socket pooling:** Multiple tracker instances for the same URL should share a single WebSocket connection to avoid excessive connections.

### WS Announce with Offers

The announce message is the primary message type. In WebSocket tracker protocol, announces include WebRTC SDP offers that the tracker will relay to other peers.

#### Client to Tracker: Announce Request

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "<20-byte binary string>",
  "numwant": 5,
  "uploaded": 0,
  "downloaded": 0,
  "left": 1048576,
  "event": "started",
  "offers": [
    {
      "offer": {
        "type": "offer",
        "sdp": "v=0\r\no=- 123456 2 IN IP4 127.0.0.1\r\n..."
      },
      "offer_id": "<20-byte binary string>"
    },
    {
      "offer": {
        "type": "offer",
        "sdp": "v=0\r\no=- 789012 2 IN IP4 127.0.0.1\r\n..."
      },
      "offer_id": "<20-byte binary string>"
    }
  ]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `action` | String | Yes | Always `"announce"` |
| `info_hash` | Binary String | Yes | 20-byte info hash (see [Binary String Encoding](#ws-binary-string-encoding)) |
| `peer_id` | Binary String | Yes | 20-byte peer ID |
| `numwant` | Integer | No | Desired number of peers. The offers array length determines actual offer count. Capped at 5 in the reference implementation. |
| `uploaded` | Integer | No | Total bytes uploaded |
| `downloaded` | Integer | No | Total bytes downloaded |
| `left` | Integer | No | Bytes remaining to download |
| `event` | String | No | `"started"`, `"stopped"`, `"completed"`, or omitted for regular announces |
| `offers` | Array | No | Array of SDP offer objects to relay to peers. Omit for stop events. |
| `trackerid` | String | No | Tracker session ID from a previous response |

**Each offer object:**

| Field | Type | Description |
|-------|------|-------------|
| `offer` | Object | WebRTC SDP offer (`{type: "offer", sdp: "..."}`) |
| `offer_id` | Binary String | 20-byte random identifier for this offer. Used to match answers back to offers. |

**Offer count:** The reference implementation limits offers to `Math.min(numwant, 5)`. More offers means more SDP generation overhead in the browser.

#### Tracker to Client: Announce Response

After processing an announce, the tracker sends back swarm statistics:

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "complete": 15,
  "incomplete": 47,
  "interval": 120
}
```

| Field | Type | Description |
|-------|------|-------------|
| `action` | String | `"announce"` |
| `info_hash` | Binary String | The info hash this response is for |
| `complete` | Integer | Number of seeders |
| `incomplete` | Integer | Number of leechers |
| `interval` | Integer | Seconds until next announce. Reference default: 120 seconds. |

### WS Offer Relay

When the tracker receives an announce with offers, it selects peers from the swarm and relays one offer to each selected peer.

#### Tracker to Target Peer: Offer Message

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "<offering peer's 20-byte ID>",
  "offer": {
    "type": "offer",
    "sdp": "v=0\r\n..."
  },
  "offer_id": "<20-byte binary string>"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `action` | String | `"announce"` (same action type) |
| `info_hash` | Binary String | The torrent's info hash |
| `peer_id` | Binary String | Peer ID of the peer that sent the offer (NOT the recipient) |
| `offer` | Object | The WebRTC SDP offer to process |
| `offer_id` | Binary String | The unique identifier for this offer |

**The target peer should:**
1. Check if it wants to connect to this peer (same torrent, not already connected)
2. Create a WebRTC peer connection
3. Set the received SDP as the remote description
4. Create an SDP answer
5. Send the answer back through the tracker

### WS Answer Relay

When a peer receives an offer and creates an answer, it sends the answer through the tracker back to the offering peer.

#### Target Peer to Tracker: Answer Message

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "<answering peer's 20-byte ID>",
  "to_peer_id": "<offering peer's 20-byte ID>",
  "answer": {
    "type": "answer",
    "sdp": "v=0\r\n..."
  },
  "offer_id": "<20-byte binary string>"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `action` | String | Yes | `"announce"` |
| `info_hash` | Binary String | Yes | The torrent's info hash |
| `peer_id` | Binary String | Yes | The answering peer's ID |
| `to_peer_id` | Binary String | Yes | The offering peer's ID (where to relay the answer) |
| `answer` | Object | Yes | WebRTC SDP answer (`{type: "answer", sdp: "..."}`) |
| `offer_id` | Binary String | Yes | Must match the offer_id from the offer being answered |
| `trackerid` | String | No | Tracker session ID |

#### Tracker to Offering Peer: Answer Relay

The tracker forwards the answer to the original offering peer:

```json
{
  "action": "announce",
  "info_hash": "<20-byte binary string>",
  "peer_id": "<answering peer's 20-byte ID>",
  "answer": {
    "type": "answer",
    "sdp": "v=0\r\n..."
  },
  "offer_id": "<20-byte binary string>"
}
```

The offering peer matches the `offer_id` to its pending offer and completes the WebRTC handshake.

### WS Scrape

#### Client to Tracker: Scrape Request

Single torrent:
```json
{
  "action": "scrape",
  "info_hash": "<20-byte binary string>"
}
```

Multiple torrents:
```json
{
  "action": "scrape",
  "info_hash": [
    "<20-byte binary string 1>",
    "<20-byte binary string 2>"
  ]
}
```

#### Tracker to Client: Scrape Response

```json
{
  "action": "scrape",
  "files": {
    "<hex info hash>": {
      "complete": 15,
      "incomplete": 47,
      "downloaded": 1234
    }
  }
}
```

### Error Response

```json
{
  "action": "error",
  "info_hash": "<20-byte binary string>",
  "message": "Invalid info_hash"
}
```

### WS Binary String Encoding

Binary data fields (`info_hash`, `peer_id`, `offer_id`) require special handling because JSON is a text format.

**Current implementation:** The reference WebTorrent tracker uses "binary strings" - each byte of the 20-byte value is represented as a single character in the string using its raw code point value. This is effectively Latin-1 (ISO 8859-1) encoding where each byte maps to a character.

```
info_hash bytes: [0x12, 0x34, 0x56, 0x78, ...]
binary string:   "\x12\x34\x56\x78..."
```

**Important caveats:**
- This encoding can cause issues with UTF-8 WebSocket text frames, since some byte sequences are not valid UTF-8
- There is an open proposal (webtorrent/webtorrent#1676) to switch to hexadecimal encoding for these fields
- The tracker server internally converts to/from hex using `bin2hex()` / `hex2bin()` utility functions
- Implementations should be prepared to handle both binary string and hex-encoded formats

**Validation rules (server-side):**
- `info_hash`: Must be exactly 20 characters (bytes)
- `peer_id`: Must be exactly 20 characters (bytes)
- `offer_id`: Must be exactly 20 characters (bytes)
- `to_peer_id` (in answer messages): Must be exactly 20 characters (bytes)

### Complete WebSocket Exchange Sequence

```
Peer A                        Tracker                       Peer B
  |                              |                              |
  |--- WS Connect -------------->|                              |
  |                              |<-- WS Connect ---------------|
  |                              |                              |
  |--- Announce ----------------->|                              |
  |    info_hash, peer_id        |                              |
  |    event: "started"          |                              |
  |    offers: [                 |                              |
  |      {offer: SDP_1,          |                              |
  |       offer_id: ID_1}       |                              |
  |    ]                         |                              |
  |                              |                              |
  |<-- Announce Response ---------|                              |
  |    complete: 1               |                              |
  |    incomplete: 1             |                              |
  |    interval: 120             |                              |
  |                              |                              |
  |                              |--- Offer Relay ------------->|
  |                              |    peer_id: A's ID           |
  |                              |    offer: SDP_1              |
  |                              |    offer_id: ID_1            |
  |                              |                              |
  |                              |<-- Answer -------------------|
  |                              |    peer_id: B's ID           |
  |                              |    to_peer_id: A's ID        |
  |                              |    answer: SDP_answer        |
  |                              |    offer_id: ID_1            |
  |                              |                              |
  |<-- Answer Relay --------------|                              |
  |    peer_id: B's ID           |                              |
  |    answer: SDP_answer        |                              |
  |    offer_id: ID_1            |                              |
  |                              |                              |
  |===== WebRTC Data Channel established directly =============>|
  |                              |                              |
  |<========= BitTorrent protocol over WebRTC =================>|
```

### Comparison with HTTP/UDP Semantics

| Feature | HTTP Tracker | UDP Tracker | WebSocket Tracker |
|---------|-------------|-------------|-------------------|
| **Transport** | HTTP GET | UDP datagrams | WebSocket (persistent) |
| **Data format** | Bencoded | Binary (big-endian) | JSON |
| **Connection state** | Stateless (per request) | Connection ID (1-2 min) | Persistent WebSocket |
| **Peer discovery** | Returns peer list | Returns peer list | Relays SDP offers/answers |
| **Signaling** | None (peers connect directly) | None (peers connect directly) | WebRTC SDP relay |
| **Compact peers** | Optional (BEP 23) | Always compact | N/A (peers connect via WebRTC) |
| **Scrape** | BEP 48 | BEP 15 | JSON scrape message |
| **Authentication** | URL parameters | BEP 41 extensions | URL parameters |
| **Reconnection** | N/A (stateless) | New connect handshake | Exponential backoff |
| **Default interval** | Tracker-defined (often 1800s) | Tracker-defined | ~120s (reference impl) |
| **IPv6 support** | `peers6` key | 18-byte peer entries | Via WebRTC (transparent) |
| **Browser support** | No (CORS issues) | No (no UDP in browser) | Yes (primary purpose) |
| **Event values** | Strings | Integers (different order) | Strings (same as HTTP) |

**Key architectural difference:** HTTP and UDP trackers are passive - they collect peer info and hand out peer lists. The WebSocket tracker is active - it relays signaling data in real time to enable WebRTC connections. After the WebRTC connection is established, the tracker is no longer involved in data transfer.

### Server-Side Behavior — Verified Against JS Reference

The WebSocket tracker has no formal BEP, so the JS reference (`webtorrent/bittorrent-tracker`'s `lib/server/parse-websocket.js` + `server.js`) defines the protocol. The WebTorrent community largely lacks a written behavioral spec. The behaviors below are observed against the JS reference (`bittorrent-tracker` npm package, run locally) by `D:\users\tj\Projects\SpawnDev.WebTorrent\tracker-debug\verify-tracker-parity.mjs`. SpawnDev.WebTorrent + SpawnDev.RTC.Server match each one.

#### 1. Announce response: NO `peers` field on the WebSocket-tracker path

The HTTP/UDP tracker response carries a `peers` list. The WebSocket-tracker response **does NOT** — peer discovery on this path happens exclusively via offer/answer relay below.

```jsonc
// Sent to the announcing client after every announce that is NOT an answer-relay or a scrape:
{
  "action":     "announce",
  "info_hash":  "<20-byte binary string>",
  "interval":   120,
  "complete":   <number of seeders in the room>,
  "incomplete": <number of leechers in the room>
  // NO `peers` field. Including one is a divergence from the reference.
  // NO `tracker_id` field by default (the JS reference does not emit one for WS).
}
```

If a server emits a `peers` field (whether `[]`, `null`, or a populated list), JS WebTorrent clients that strictly follow the reference may treat it as an unrecognized field; client behavior in that case is undefined.

#### 2. Answer-relay path: server sends NO response to the answer-sender

When a client sends an announce with `answer + to_peer_id + offer_id` (a reply to a previously-received offer-relay), the server forwards the answer to the targeted peer **and returns nothing to the sender**. There is no `interval/complete/incomplete` reply for this announce.

```jsonc
// Client to server (answer-relay):
{
  "action":     "announce",
  "info_hash":  "...",
  "peer_id":    "<answering peer 20 bytes>",
  "answer":     { "type": "answer", "sdp": "v=0\r\n..." },
  "to_peer_id": "<offering peer 20 bytes>",
  "offer_id":   "<20-byte binary string matching the original offer>"
}

// Server to original offering peer (forwarded answer):
{
  "action":    "announce",
  "info_hash": "...",
  "peer_id":   "<answering peer 20 bytes>",
  "answer":    { "type": "answer", "sdp": "v=0\r\n..." },
  "offer_id":  "<same 20-byte id>"
}

// Server to answer-sender: NOTHING. Do not respond.
```

A server that responds to the answer-sender with a counts frame produces an extra spurious announce frame the client never expects. Clients tolerant of unknown frames will ignore it; clients that strictly track expected frame sequences may desynchronize.

#### 3. Stopped event: server DOES send a response, with post-stop counts

When a client announces with `event=stopped`, the server removes the peer from the room **AND** sends back a normal announce response carrying the updated counts (`incomplete` reflects post-removal). The response shape is identical to the regular announce response (no `peers` field).

```jsonc
// Client to server (graceful leave):
{
  "action":    "announce",
  "info_hash": "...",
  "peer_id":   "...",
  "event":     "stopped",
  "left":      0
}

// Server to client (response with post-stop counts):
{
  "action":     "announce",
  "info_hash":  "...",
  "interval":   120,
  "complete":   <updated, this peer no longer counted>,
  "incomplete": <updated, this peer no longer counted>
}
```

Offer forwarding is **skipped** on a stopped announce regardless of whether the announce carried offers (which it should not, but the server defensively ignores them).

#### 4. Offer forwarding: random pick, one offer per existing peer, capped at the offer count

When a peer announces with `offers: [...]`, the server selects existing peers in the room (excluding the announcer itself) in **random order** and forwards one offer per selected peer until either the offer array runs out or the candidate list runs out. Each forwarded message is shaped:

```jsonc
{
  "action":    "announce",
  "info_hash": "...",
  "peer_id":   "<announcer's peer_id, NOT recipient's>",
  "offer":     { "type": "offer", "sdp": "..." },
  "offer_id":  "..."
}
```

The recipient uses the inbound `peer_id + offer_id` to know who to address the answer back to via `to_peer_id`.

`numwant` is informational from the announce; the actual forwarding count is `min(offers.length, candidates.length)`. The reference does not pad with empty offers and does not duplicate-assign.

#### 5. Reconnect with same `peer_id`: clean overwrite

When a peer reconnects (new WebSocket) and announces with the same `peer_id` for the same `info_hash`, the room's peer-id-to-socket binding is overwritten cleanly. The previous socket (if still alive) does not receive any forwarded offer for the new announce; only the current socket is registered as that peer.

#### 6. Room isolation: strict per-`info_hash` partitioning

A peer's announce only affects the room keyed by its `info_hash`. The same peer can be in multiple rooms simultaneously by announcing with different info hashes — these are tracked independently; no cross-room offer leakage.

#### 7. `event=completed` and `left=0` mark the peer as a seeder for swarm-stats purposes

`complete` in the response counts peers that have either announced `event=completed` at some point in their session OR currently report `left=0`. `incomplete` counts everyone else.

#### 8. Frame size limit

The reference and SpawnDev.RTC.Server both cap an inbound frame at 1 MiB (`MaxMessageBytes` default, configurable). Frames exceeding this cap are dropped silently (the connection may be closed by the server depending on implementation).

#### 9. `action=scrape` over WebSocket

The reference accepts a JSON scrape message (`{action: "scrape", info_hash: "..."}` or array form) and replies with a `{action: "scrape", files: { ... }}` per-info-hash counts response. SpawnDev.RTC.Server's `TrackerSignalingServer` does not currently dispatch `action=scrape` — incoming scrape frames hit the "unknown action" branch and are silently ignored. WebTorrent JS clients fall back to assuming scrape unsupported. Tracked as a follow-up.

#### Parity harness

The harness driving these findings lives at:

```
D:\users\tj\Projects\SpawnDev.WebTorrent\tracker-debug\
  verify-tracker-parity.mjs   - Six scenarios (A/B/C/D/E/F) head-to-head
                                 against the live hub AND a fresh local
                                 bittorrent-tracker reference. Diffs every
                                 captured frame and reports per-scenario
                                 divergences.
  verify-offer-flow.mjs        - Three-peer offer/forward flow against an
                                 explicit tracker URL.
  verify-offer-flow-local.mjs  - Same but co-runs the JS reference and
                                 captures both server-side and client-side
                                 traffic.
```

Run before any change to `TrackerSignalingServer.HandleAnnounceAsync` or the wire-message DTOs. Run after any redeploy of `hub.spawndev.com`. The harness is fast (<10s end-to-end) and produces a clean PASS/FAIL summary.

---

## Implementation Notes for SpawnDev.WebTorrent

### Multi-Tracker Support

SpawnDev.WebTorrent needs to support all three tracker types simultaneously:

- **WebSocket trackers** (wss://): Required for browser peers (WebRTC signaling)
- **HTTP trackers** (http:// / https://): Required for desktop peers and traditional swarms
- **UDP trackers** (udp://): Required for desktop peers (most efficient protocol)

The announce-list in a .torrent file may contain a mix of all three types. The client must detect the protocol from the URL scheme and use the appropriate protocol handler.

### Tracker URL Detection

```
wss://tracker.example.com   -> WebSocket tracker
ws://tracker.example.com    -> WebSocket tracker (insecure)
http://tracker.example.com  -> HTTP tracker
https://tracker.example.com -> HTTP tracker (TLS)
udp://tracker.example.com   -> UDP tracker
```

### Browser vs Desktop Considerations

| Capability | Browser (Blazor WASM) | Desktop (.NET) |
|------------|----------------------|----------------|
| WebSocket trackers | Yes | Yes |
| HTTP trackers | Limited (CORS) | Yes |
| UDP trackers | No | Yes |
| Direct TCP peers | No | Yes |
| WebRTC peers | Yes | Yes (via SipSorcery) |

In browser contexts, WebSocket trackers are typically the only viable tracker type. HTTP trackers may work if the tracker sends appropriate CORS headers, but most do not. UDP is impossible in browsers.

### Announce Interval Management

- Always respect the `interval` value from tracker responses
- If `min interval` is provided (HTTP), never announce more frequently
- For WebSocket trackers, the default interval is shorter (120s vs 1800s) because the connection is persistent and cheap
- Implement jitter to avoid thundering herd: add random 0-30 seconds to the interval

### Error Handling

- **HTTP tracker errors:** Check for `failure reason` key first, then process normally
- **UDP tracker errors:** Check `action` field for value `3`; read message string from remaining bytes
- **WebSocket tracker errors:** Check for `action: "error"` and read `message` field
- **Timeout handling:** UDP has explicit retry algorithm (15*2^n). HTTP and WebSocket should implement similar backoff.

---

## References

- [BEP 3 - The BitTorrent Protocol Specification](https://www.bittorrent.org/beps/bep_0003.html)
- [BEP 15 - UDP Tracker Protocol for BitTorrent](https://www.bittorrent.org/beps/bep_0015.html)
- [BEP 23 - Tracker Returns Compact Peer Lists](https://www.bittorrent.org/beps/bep_0023.html)
- [BEP 41 - UDP Tracker Protocol Extensions](https://www.bittorrent.org/beps/bep_0041.html)
- [BEP 48 - Tracker Protocol Extension: Scrape](https://www.bittorrent.org/beps/bep_0048.html)
- [BitTorrent Specification - Theory.org Wiki](https://wiki.theory.org/BitTorrentSpecification)
- [WebTorrent bittorrent-tracker](https://github.com/webtorrent/bittorrent-tracker)
- [WebTorrent BEP Proposal (PR #881)](https://github.com/webtorrent/webtorrent/pull/881)
- [WebSocket Tracker Protocol Discussion (Issue #257)](https://github.com/webtorrent/bittorrent-tracker/issues/257)
- [Hex Encoding Proposal (Issue #1676)](https://github.com/webtorrent/webtorrent/issues/1676)
