# BitTorrent Extension Protocols

Comprehensive reference for the extension protocols used in peer-to-peer communication within BitTorrent swarms. This document covers BEP 10 (Extension Protocol framework), BEP 9 (ut_metadata), BEP 11 (ut_pex), and BEP 54 (lt_donthave).

---

## Table of Contents

1. [BEP 10 - Extension Protocol Framework](#bep-10---extension-protocol-framework)
2. [BEP 9 - ut_metadata (Metadata Exchange)](#bep-9---ut_metadata-metadata-exchange)
3. [BEP 11 - ut_pex (Peer Exchange)](#bep-11---ut_pex-peer-exchange)
4. [BEP 54 - lt_donthave](#bep-54---lt_donthave)
5. [Implementation Notes for SpawnDev.WebTorrent](#implementation-notes-for-spawndevwebtorrent)

---

## BEP 10 - Extension Protocol Framework

**Status:** Accepted  
**Specification:** https://www.bittorrent.org/beps/bep_0010.html

BEP 10 defines the framework that all extension messages (ut_metadata, ut_pex, lt_donthave, etc.) are built on. Without BEP 10, none of the other extensions in this document can function.

### Signaling Support

Support for the extension protocol is advertised in the BitTorrent handshake reserved bytes. Bit 20 (counting from the right, starting at 0) of the 8-byte reserved field must be set:

```
Reserved bytes: 0x00 0x00 0x00 0x00 0x00 0x10 0x00 0x00
                                          ^^^^
                                    Bit 20 set (0x10 in byte 5)
```

### Message Framing

All extension messages use BitTorrent message type `20`. The wire format is:

```
+--------------------+----------+-------------------+-----------+
| Length Prefix (4B) | Type (1B)| Ext Msg ID (1B)   | Payload   |
+--------------------+----------+-------------------+-----------+
|   big-endian u32   |   0x14   | 0x00 = handshake  | bencoded  |
|   (total - 4)      |   (20)   | 0x01+ = extension | dict or   |
|                    |          |                   | raw bytes |
+--------------------+----------+-------------------+-----------+
```

- **Length prefix** (4 bytes, big-endian): Total message length minus the 4-byte prefix itself
- **Message type** (1 byte): Always `20` (0x14) for extension messages
- **Extended message ID** (1 byte): `0` for the extension handshake, or the locally assigned ID for a specific extension
- **Payload** (variable): Bencoded dictionary for handshake; extension-specific format otherwise

### Extension Handshake (Extended Message ID 0)

Sent immediately after the standard BitTorrent handshake completes, to any peer that advertised BEP 10 support. The payload is a bencoded dictionary.

#### Required Field

| Field | Type | Description |
|-------|------|-------------|
| `m` | Dictionary | Maps extension names (strings) to local extended message IDs (integers). Setting an ID to `0` disables that extension. |

#### Optional Fields

| Field | Type | Description |
|-------|------|-------------|
| `p` | Integer | Local TCP listen port (useful when connecting through NAT) |
| `v` | UTF-8 String | Client name and version (e.g., `"SpawnDev.WebTorrent 3.0.0"`) |
| `yourip` | 4 or 16 bytes | External IP address of the receiving peer (compact binary) |
| `ipv4` | 4 bytes | Sender's IPv4 address (compact binary) |
| `ipv6` | 16 bytes | Sender's IPv6 address (compact binary) |
| `reqq` | Integer | Number of outstanding requests this client supports (default: 250) |
| `metadata_size` | Integer | Size of the info dictionary in bytes (used by ut_metadata / BEP 9) |

#### Example Handshake

```
Bencoded:
d
  1:md
    11:ut_metadatai1e
    6:ut_pexi2e
    12:lt_donthavei3e
  e
  13:metadata_sizei31235e
  1:pi6881e
  1:v25:SpawnDev.WebTorrent 3.0.0
e
```

Decoded:
```
{
  "m": {
    "ut_metadata": 1,
    "ut_pex": 2,
    "lt_donthave": 3
  },
  "metadata_size": 31235,
  "p": 6881,
  "v": "SpawnDev.WebTorrent 3.0.0"
}
```

This tells the remote peer:
- Send ut_metadata messages using extended message ID `1`
- Send ut_pex messages using extended message ID `2`
- Send lt_donthave messages using extended message ID `3`
- The torrent's info dictionary is 31,235 bytes

#### Key Rules

1. **IDs are local.** Each peer assigns its own IDs. Peer A might assign ut_metadata=1, while Peer B assigns ut_metadata=3. When A sends a ut_metadata message to B, it uses B's ID (3), not its own.
2. **Disabling extensions.** Send `0` as the ID to disable an extension: `{'m': {'ut_pex': 0}}` means "stop sending me PEX messages."
3. **Re-sending handshake.** The extension handshake can be re-sent at any time during the connection. The `m` dictionary in subsequent handshakes is additive - only include changed mappings.
4. **Extension naming.** Extensions should be prefixed with a two-character client code (e.g., `ut_` for uTorrent, `lt_` for libtorrent). One and two-byte identifiers are reserved for future BEP use.

---

## BEP 9 - ut_metadata (Metadata Exchange)

**Status:** Accepted  
**Specification:** https://www.bittorrent.org/beps/bep_0009.html  
**Extension name:** `ut_metadata`

### Purpose

Allows peers to download the torrent's info dictionary (metadata) directly from other peers in the swarm. This is the mechanism that makes magnet links work - a client can join a swarm with only an info hash and retrieve the full metadata needed to begin downloading.

### Metadata Structure

- The "metadata" is the bencoded info dictionary from the .torrent file
- It is divided into **16 KiB (16,384 byte) blocks**, called "pieces"
- Pieces are indexed starting at **0**
- The last piece may be smaller than 16 KiB
- All other pieces are exactly 16,384 bytes

**Piece count calculation:**
```
piece_count = ceil(metadata_size / 16384)
```

**Last piece size:**
```
last_piece_size = metadata_size - (16384 * (piece_count - 1))
```

Example: For `metadata_size = 31235`:
- `piece_count = ceil(31235 / 16384) = 2`
- Piece 0: 16,384 bytes
- Piece 1: 31,235 - 16,384 = 14,851 bytes

### Extension Handshake

Peers that support ut_metadata include two entries in the BEP 10 handshake:

```
{
  "m": {
    "ut_metadata": <local_message_id>
  },
  "metadata_size": <total_metadata_bytes>
}
```

- `metadata_size` is the total size in bytes of the info dictionary
- If a peer does not yet have the metadata (e.g., it is also downloading via magnet link), it may omit `metadata_size` or set it to `0`

### Message Format

All ut_metadata messages are sent as BEP 10 extension messages (type 20). The payload begins with a bencoded dictionary, optionally followed by raw binary data.

```
Wire format:
+----------+--------+---------------------+------------------+
| Len (4B) | 20 (1B)| Ext ID (1B)         | Payload          |
+----------+--------+---------------------+------------------+
                     | peer's ut_metadata  | bencoded dict    |
                     | message ID          | [+ binary data]  |
+----------+--------+---------------------+------------------+
```

The bencoded dictionary always contains:

| Field | Type | Description |
|-------|------|-------------|
| `msg_type` | Integer | 0 = request, 1 = data, 2 = reject |
| `piece` | Integer | Metadata piece index (0-based) |

### Message Type 0: Request

Requests a specific metadata piece from the peer.

**Bencoded payload:**
```
d8:msg_typei0e5:piecei0ee
```

**Decoded:**
```json
{ "msg_type": 0, "piece": 0 }
```

**State machine for sender:**
1. Connect to peer, complete BEP 10 handshake
2. Check if peer advertised `ut_metadata` and `metadata_size > 0`
3. Send request for each piece index from `0` to `piece_count - 1`
4. Track which pieces are outstanding
5. Handle data or reject responses

**Behavior:**
- The requesting peer must know which pieces it still needs
- Clients should spread requests across multiple peers for faster metadata assembly
- Flood protection: implementations may reject requests after a certain number in a time window

### Message Type 1: Data

Responds with the requested metadata piece.

**Bencoded payload + binary data:**
```
d8:msg_typei1e5:piecei0e10:total_sizei31235ee<16384 bytes of metadata>
```

**Decoded (dictionary portion):**
```json
{ "msg_type": 1, "piece": 0, "total_size": 31235 }
```

| Field | Type | Description |
|-------|------|-------------|
| `msg_type` | Integer | `1` |
| `piece` | Integer | Piece index being delivered |
| `total_size` | Integer | Total metadata size in bytes |

**Critical detail:** The raw metadata bytes are appended directly after the bencoded dictionary, NOT inside it. The bencoded dictionary ends, and the remaining bytes in the message are the metadata piece content.

**Parsing a data message:**
1. Parse the bencoded dictionary from the start of the payload
2. Note how many bytes the bencoded dictionary consumed
3. The remaining bytes (from end of dictionary to end of message) are the metadata piece
4. Verify the piece size matches expectations:
   - For pieces 0 through `piece_count - 2`: exactly 16,384 bytes
   - For the last piece: `total_size - (16384 * piece_index)` bytes

### Message Type 2: Reject

Indicates the peer does not have the requested metadata piece.

**Bencoded payload:**
```
d8:msg_typei2e5:piecei0ee
```

**Decoded:**
```json
{ "msg_type": 2, "piece": 0 }
```

**When to send reject:**
- The peer does not have the complete metadata (also downloading via magnet)
- Flood protection triggered (too many requests from this peer)
- The piece index is out of range

### Verification

After all pieces are received, the client must verify the reassembled metadata:

1. Concatenate all pieces in order (piece 0 + piece 1 + ... + piece N)
2. Compute SHA-1 hash of the concatenated result
3. Compare with the info hash from the magnet link
4. If they match, the metadata is valid - parse it and begin downloading
5. If they do not match, discard all pieces and re-request from different peers

For v2 torrents (BEP 52), verification uses SHA-256 instead of SHA-1.

### Edge Cases

**Peer does not have metadata:**
- The peer omits `metadata_size` from the handshake, or sets it to `0`
- Any requests to this peer should receive reject responses
- The requesting client should try other peers

**`metadata_size` changes between peers:**
- Different peers may report different `metadata_size` values
- This should not happen in a healthy swarm since the metadata is deterministic for a given info hash
- If it does happen, trust the `total_size` field in data responses and always verify via SHA-1 hash
- Discard metadata from peers whose reported size does not match the majority

**Peer disconnects during transfer:**
- Track which pieces are complete and which are pending
- Re-request pending pieces from other peers
- Already-received pieces remain valid (they will be verified at the end)

**Metadata is very large:**
- Some torrents have info dictionaries of several megabytes
- Implementations should limit the maximum metadata size they accept (e.g., 10 MB)
- With 16 KiB pieces, a 10 MB metadata requires ~640 pieces

### Complete Exchange Sequence

```
Peer A (has metadata)                    Peer B (needs metadata)
    |                                         |
    |<--- BT Handshake (reserved bit 20) ---->|
    |                                         |
    |--- Ext Handshake ---------------------->|
    |    m: {ut_metadata: 2}                  |
    |    metadata_size: 31235                 |
    |                                         |
    |<-- Ext Handshake -----------------------|
    |    m: {ut_metadata: 1}                  |
    |    (no metadata_size - doesn't have it) |
    |                                         |
    |<-- Request (msg_type=0, piece=0) -------|  (to ext ID 2)
    |<-- Request (msg_type=0, piece=1) -------|  (to ext ID 2)
    |                                         |
    |--- Data (msg_type=1, piece=0) --------->|  (to ext ID 1)
    |    total_size=31235                     |
    |    + 16384 bytes of metadata            |
    |                                         |
    |--- Data (msg_type=1, piece=1) --------->|  (to ext ID 1)
    |    total_size=31235                     |
    |    + 14851 bytes of metadata            |
    |                                         |
    |    Peer B: SHA-1(piece0 + piece1)       |
    |           == info_hash? Valid!           |
```

### Magnet URI Format

ut_metadata is the mechanism behind magnet links:

**v1 magnet (BEP 3):**
```
magnet:?xt=urn:btih:<40-char-hex-info-hash>&dn=<display-name>&tr=<tracker-url>&x.pe=<peer-address>
```

**v2 magnet (BEP 52):**
```
magnet:?xt=urn:btmh:<tagged-multihash>&dn=<display-name>&tr=<tracker-url>&x.pe=<peer-address>
```

| Parameter | Required | Description |
|-----------|----------|-------------|
| `xt` | Yes | Exact Topic - the info hash (`urn:btih:` for v1, `urn:btmh:` for v2) |
| `dn` | No | Display Name - human-readable name for the torrent |
| `tr` | No | Tracker URL (can appear multiple times) |
| `x.pe` | No | Peer address in `host:port` format (can appear multiple times) |

---

## BEP 11 - ut_pex (Peer Exchange)

**Status:** Accepted  
**Specification:** https://www.bittorrent.org/beps/bep_0011.html  
**Extension name:** `ut_pex`

### Purpose

Peer Exchange allows peers to share information about other peers they are connected to, providing a decentralized peer discovery mechanism that complements trackers and DHT. Instead of every peer asking the tracker, connected peers tell each other about the peers they know.

### Extension Handshake

Negotiated via the BEP 10 extension handshake:

```
{
  "m": {
    "ut_pex": <local_message_id>
  }
}
```

### Message Format

PEX messages are sent as BEP 10 extension messages. The payload is a bencoded dictionary with the following fields:

| Field | Type | Description |
|-------|------|-------------|
| `added` | String (binary) | IPv4 peers added, compact format (6 bytes each) |
| `added.f` | String (binary) | Flags for each IPv4 peer added (1 byte each) |
| `added6` | String (binary) | IPv6 peers added, compact format (18 bytes each) |
| `added6.f` | String (binary) | Flags for each IPv6 peer added (1 byte each) |
| `dropped` | String (binary) | IPv4 peers dropped, compact format (6 bytes each) |
| `dropped6` | String (binary) | IPv6 peers dropped, compact format (18 bytes each) |

A message must contain at least one of: `added`, `added6`, `dropped`, or `dropped6`.

### Compact Peer Encoding

**IPv4 compact format** (6 bytes per peer):
```
+------------------+------------+
| IPv4 Address (4B)| Port (2B)  |
+------------------+------------+
| network order    | big-endian |
+------------------+------------+
```

Example: Peer at `192.168.1.100:6881`
```
Bytes: C0 A8 01 64 1A E1
       ^^^^^^^^^^^  ^^^^
       192.168.1.100  6881
```

**IPv6 compact format** (18 bytes per peer):
```
+------------------+------------+
| IPv6 Address(16B)| Port (2B)  |
+------------------+------------+
| network order    | big-endian |
+------------------+------------+
```

### Flags Byte Format

Each peer in the `added` or `added6` field has a corresponding byte in `added.f` or `added6.f`. The flags byte uses individual bits:

| Bit | Hex Value | Meaning |
|-----|-----------|---------|
| 0 | `0x01` | Peer prefers encrypted connections |
| 1 | `0x02` | Peer is a seed (upload-only) |
| 2 | `0x04` | Peer supports uTP |
| 3 | `0x08` | Peer supports ut_holepunch (for NAT traversal) |
| 4 | `0x10` | Outgoing connection - this peer is reachable (we connected to them) |
| 5-7 | - | Reserved (must not be set without a BEP amendment) |

**Flags byte examples:**
```
0x00 = No flags
0x01 = Prefers encryption
0x02 = Is a seed
0x03 = Prefers encryption + is a seed
0x05 = Prefers encryption + supports uTP
0x12 = Is a seed + outgoing (reachable)
0x15 = Prefers encryption + supports uTP + outgoing
```

**Important:** The `added.f` string must have exactly the same number of bytes as `added` has peers (i.e., `len(added.f) == len(added) / 6`). Same rule applies for `added6.f` and `added6`.

### Timing and Rate Limiting

**Send interval:** Clients must batch updates to send no more than **1 PEX message per minute**. This prevents PEX from generating excessive traffic.

**Peer limits per message** (except the initial message):
- Combined `added` + `added6`: maximum **50 entries**
- Combined `dropped` + `dropped6`: maximum **50 entries**

**Initial message:** The first PEX message after handshake may exceed these limits to bootstrap the peer with the current swarm state. There is no strict requirement to send a PEX message immediately after the handshake - the client may wait until it has sufficient peer connections.

### Peer Inclusion Rules

**When to add a peer:**
- A peer must only be included in the `added` field once a connection to that peer is **successfully established** (full BitTorrent handshake completed)
- Do not add peers from unverified sources (e.g., other PEX messages) - only add peers you have directly connected to

**When to drop a peer:**
- Include a peer in the `dropped` field once it is **disconnected**
- Only signaling added peers without ever dropping them is **non-compliant behavior**

**Constraints within a single message:**
- `added` and `dropped` must contain no duplicate addresses
- The same address must not appear in both `added` and `dropped` in the same message

### Event Elision

Transient connect-disconnect cycles that happen between PEX update intervals can be safely elided. If Peer C connects and disconnects between two PEX messages, neither an add nor a drop needs to be sent for Peer C. This is because PEX only needs to convey the net state change between messages.

Implementation approaches:
1. **Queue events per peer:** Track the latest event (connected/disconnected) per peer, only send the net change
2. **Timeline approach:** Maintain a per-torrent timeline of events with a sent-status pointer

### Underpopulated Swarms

When a client has fewer than 25 live connections for an address family (IPv4 or IPv6), it may relax the rules:

- Maintain a list of up to **25 recently disconnected, fully handshaken peers**
- These peers may be included in `added` fields (even though they are currently disconnected) to help bootstrap other peers
- Drain included contacts from the recently-seen list immediately
- Drop these peers in the next PEX message
- This relaxation applies independently to IPv4 and IPv6

### Private Torrents

**PEX must be disabled for private torrents.** Per BEP 27 (Private Torrents), when a torrent's info dictionary contains `"private": 1`, clients must:

- NOT send any PEX messages for that torrent
- NOT process any incoming PEX messages for that torrent
- Only discover peers through the private tracker

This is mandatory because PEX would bypass the tracker's access control, potentially allowing unauthorized peers to discover and join the swarm.

### Security Considerations

PEX data is **untrusted** and potentially malicious. Implementations should:

1. **Diversify sources:** Avoid sourcing all connection candidates from a single PEX source
2. **Ignore suspicious entries:** Discard duplicate IP addresses that appear with different ports
3. **Use BEP 40 (Canonical Peer Priority):** Distribute connection attempts across different subnets to resist poisoning attacks
4. **Rate-limit connections:** Do not immediately connect to all peers received via PEX

### Complete PEX Message Example

```
Bencoded:
d
  5:added12:<6 bytes peer1><6 bytes peer2>
  7:added.f2:<flags1><flags2>
  6:added618:<18 bytes ipv6_peer>
  8:added6.f1:<flags>
  7:dropped6:<6 bytes old_peer>
e
```

**Decoded (conceptual):**
```
{
  "added": [192.168.1.100:6881, 10.0.0.5:51413],
  "added.f": [0x12, 0x04],          // seed+reachable, uTP
  "added6": [fe80::1:6881],
  "added6.f": [0x02],                // seed
  "dropped": [172.16.0.50:6889]
}
```

### State Machine

```
                  +------------------+
                  |  Connection      |
                  |  Established     |
                  +--------+---------+
                           |
                           v
                  +------------------+
                  |  BEP 10          |
                  |  Handshake       |
                  |  (check ut_pex)  |
                  +--------+---------+
                           |
              +------------+------------+
              | ut_pex supported        | ut_pex NOT supported
              v                         v
    +------------------+       +------------------+
    |  Start PEX       |       |  No PEX for      |
    |  Timer (60s)     |       |  this peer        |
    +--------+---------+       +------------------+
             |
             v
    +------------------+
    |  Collect events   |
    |  (adds/drops)    |
    +--------+---------+
             |
    +--------+---------+
    |  Timer fires     |
    |  (60s elapsed)   |
    +--------+---------+
             |
             v
    +------------------+
    |  Build PEX msg   |
    |  - Max 50 added  |
    |  - Max 50 dropped|
    |  - No duplicates |
    +--------+---------+
             |
             v
    +------------------+
    |  Send to peer    |
    |  Reset timer     |
    +--------+---------+
             |
             +-------> (loop back to Collect events)
```

---

## BEP 54 - lt_donthave

**Status:** Accepted  
**Specification:** https://www.bittorrent.org/beps/bep_0054.html  
**Extension name:** `lt_donthave`

### Purpose

Allows a peer to announce that a previously advertised piece is no longer available. Without this extension, the only way to signal that a piece has been removed is to disconnect from all peers and reconnect, which is extremely disruptive.

**Primary use cases:**
- **Streaming scenarios:** A media streaming client that discards pieces after playing them
- **Disk space management:** A client that removes old pieces to free disk space
- **Partial seeding:** A client that only keeps a subset of pieces at any time
- **Cache eviction:** Any scenario where pieces are dynamically added and removed

### Extension Handshake

Negotiated via BEP 10:
```
{
  "m": {
    "lt_donthave": <local_message_id>
  }
}
```

### Message Format

The DontHave message has a very simple format, mirroring the standard BitTorrent HAVE message:

```
Wire format:
+----------+--------+----------+-----------+
| Len (4B) | 20 (1B)| Ext (1B) | Index (4B)|
+----------+--------+----------+-----------+
| 0x00 0x00| 0x14   | peer's   | piece     |
| 0x00 0x06|        | lt_dont  | index     |
|          |        | have ID  | (u32 BE)  |
+----------+--------+----------+-----------+

Total wire size: 10 bytes (4 length + 1 type + 1 ext ID + 4 index)
Message size (excluding length prefix): 6 bytes
```

| Offset | Size | Field | Value |
|--------|------|-------|-------|
| 0 | 4 bytes | Length prefix | `6` (big-endian) |
| 4 | 1 byte | Message type | `20` (extension message) |
| 5 | 1 byte | Extended message ID | Peer's lt_donthave ID |
| 6 | 4 bytes | Piece index | Big-endian unsigned 32-bit integer |

### Asymmetric Negotiation

Unlike most extensions, lt_donthave operates asymmetrically:

- A peer **may send** a DontHave message even if it has NOT advertised lt_donthave support in its own handshake
- A peer that **has advertised** lt_donthave support must be prepared to receive DontHave messages from peers that have NOT advertised support

This means: if you advertise lt_donthave, you must handle incoming DontHave messages. But you can send DontHave messages to any peer that advertised support, regardless of whether you advertised it yourself.

### Sender Behavior

When discarding piece `n`, the sender must transmit DontHave messages to all connected peers that:

1. Have advertised lt_donthave support in their extension handshake, **AND**
2. Have previously received a BitField or Have message indicating this peer had piece `n`

**Interaction with the Fast Extension (BEP 6):**
- **Without Fast Extension:** Outstanding requests for the discarded piece are silently dropped. The requesting peer will eventually time out.
- **With Fast Extension:** Outstanding requests must receive explicit **Reject Request** messages before the DontHave is sent. This gives the requesting peer a clean signal to re-request from another peer.

### Receiver Behavior

When receiving a DontHave message for piece `n`:

1. Mark piece `n` as unavailable from this peer (clear the bit in the peer's bitfield)
2. Update piece availability counts
3. If piece `n` was being requested from this peer:
   - **Without Fast Extension:** Treat as if the peer sent Choke - silently cancel outstanding requests
   - **With Fast Extension:** Wait for explicit Reject Request messages rather than canceling proactively

### Complete Exchange Sequence

```
Peer A (streaming, discards pieces)       Peer B (downloading)
    |                                          |
    |--- Ext Handshake ----------------------->|
    |    m: {lt_donthave: 3}                   |
    |                                          |
    |<-- Ext Handshake ------------------------|
    |    m: {lt_donthave: 5}                   |
    |                                          |
    |--- Bitfield: pieces 0,1,2,3 available -->|
    |                                          |
    |    ... time passes, piece 0 played ...   |
    |    ... piece 0 discarded from disk ...   |
    |                                          |
    |--- DontHave (ext=5, index=0) ----------->|
    |                                          |
    |    Peer B: clears piece 0 from A's       |
    |            bitfield, seeks piece 0       |
    |            from other peers              |
```

---

## Implementation Notes for SpawnDev.WebTorrent

### Extension Registration Order

When implementing BEP 10, the order of extension IDs is local to each peer. A recommended convention:

```csharp
// Local extension ID assignments (example)
const int UT_METADATA_ID = 1;
const int UT_PEX_ID = 2;
const int LT_DONTHAVE_ID = 3;
```

But always use the remote peer's advertised IDs when sending messages TO that peer.

### Metadata Exchange Strategy

For magnet link resolution:
1. Connect to peers from the magnet link's `tr` (tracker) and `x.pe` (peer) parameters
2. Complete BEP 10 handshake with each peer
3. Identify peers that have `metadata_size > 0`
4. Request pieces from multiple peers in parallel (spread load)
5. Track per-peer request limits to avoid flood protection rejection
6. Verify assembled metadata via SHA-1 hash comparison with info hash
7. If verification fails, discard and retry from different peers

### PEX Implementation Checklist

- [ ] Check `private` flag in torrent info dict before enabling PEX
- [ ] Batch PEX messages to 1 per minute per peer
- [ ] Cap at 50 added + 50 dropped per message (except initial)
- [ ] Track connection state changes (add on connect, drop on disconnect)
- [ ] Set flags byte correctly (seed status, encryption, uTP, reachability)
- [ ] Handle underpopulated swarms (< 25 connections) with recently-seen list
- [ ] Never add and drop the same peer in a single message

### DontHave Considerations

For WebTorrent in browser contexts, lt_donthave is particularly relevant because:
- Browser storage (IndexedDB/OPFS) may have size limits requiring piece eviction
- Streaming playback scenarios benefit from discarding played pieces
- Memory-constrained environments cannot hold all pieces indefinitely

---

## References

- [BEP 10 - Extension Protocol](https://www.bittorrent.org/beps/bep_0010.html)
- [BEP 9 - Extension for Peers to Send Metadata Files](https://www.bittorrent.org/beps/bep_0009.html)
- [BEP 11 - Peer Exchange (PEX)](https://www.bittorrent.org/beps/bep_0011.html)
- [BEP 54 - The lt_donthave extension](https://www.bittorrent.org/beps/bep_0054.html)
- [BEP 27 - Private Torrents](https://www.bittorrent.org/beps/bep_0027.html)
- [BEP 6 - Fast Extension](https://www.bittorrent.org/beps/bep_0006.html)
- [BEP 52 - The BitTorrent Protocol Specification v2](https://www.bittorrent.org/beps/bep_0052.html)
- [libtorrent Extension Protocol](http://libtorrent.org/extension_protocol.html)
