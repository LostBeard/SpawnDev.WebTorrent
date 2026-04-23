# BitTorrent Wire Protocol - Complete Reference

> Covers BEP 3 (Base Protocol), BEP 6 (Fast Extension), and BEP 10 (Extension Protocol).
> Written for implementors building a BitTorrent client from scratch.
>
> Sources: [BEP 3](https://www.bittorrent.org/beps/bep_0003.html) |
> [BEP 6](https://www.bittorrent.org/beps/bep_0006.html) |
> [BEP 10](https://www.bittorrent.org/beps/bep_0010.html) |
> [BEP 4](https://www.bittorrent.org/beps/bep_0004.html) |
> [BEP 5](https://www.bittorrent.org/beps/bep_0005.html) |
> [Wiki Theory](https://wiki.theory.org/BitTorrentSpecification)

---

## Table of Contents

1. [Protocol Overview](#1-protocol-overview)
2. [Handshake](#2-handshake)
3. [Reserved Bytes and Extension Signaling](#3-reserved-bytes-and-extension-signaling)
4. [Message Framing](#4-message-framing)
5. [Base Protocol Messages (BEP 3)](#5-base-protocol-messages-bep-3)
6. [Connection State Machine](#6-connection-state-machine)
7. [Choking Algorithm](#7-choking-algorithm)
8. [Piece Selection](#8-piece-selection)
9. [Request Pipelining](#9-request-pipelining)
10. [Endgame Mode](#10-endgame-mode)
11. [Keep-Alive](#11-keep-alive)
12. [Fast Extension (BEP 6)](#12-fast-extension-bep-6)
13. [Extension Protocol (BEP 10)](#13-extension-protocol-bep-10)
14. [Error Handling and Disconnection](#14-error-handling-and-disconnection)
15. [Super Seeding](#15-super-seeding)
16. [Complete Message ID Reference](#16-complete-message-id-reference)
17. [Implementation Notes](#17-implementation-notes)

---

## 1. Protocol Overview

The BitTorrent peer wire protocol operates over a reliable, ordered byte stream - typically TCP, but also WebRTC data channels (WebTorrent) or uTP (BEP 29). Everything in this document applies regardless of the transport layer.

The protocol is a symmetric, binary, length-prefixed message stream. After a fixed-size handshake, peers exchange an unending stream of messages. Each peer maintains four bits of state about the connection and uses these to drive data transfer decisions.

**Key properties:**

- All integers are **big-endian** (network byte order) unless stated otherwise
- The protocol is **peer-to-peer** - both sides use the same message set
- Connections are **full-duplex** - both sides send and receive simultaneously
- The protocol is **stateful** - message validity depends on connection state

---

## 2. Handshake

The handshake is the first data exchanged on every peer connection. It is **not** length-prefixed like subsequent messages - it has a fixed format.

### 2.1 Byte Layout

```
Offset  Size  Field        Description
------  ----  -----------  ------------------------------------------------
0       1     pstrlen      Length of the protocol string (always 19)
1       19    pstr         Protocol string: "BitTorrent protocol"
20      8     reserved     Extension flags (see Section 3)
28      20    info_hash    SHA-1 hash of the bencoded info dictionary
48      20    peer_id      Client's 20-byte peer identifier
------  ----
Total:  68 bytes
```

### 2.2 Wire Bytes (Hex)

```
13                                        <- pstrlen = 19
42 69 74 54 6F 72 72 65 6E 74 20 70 72   <- "BitTorrent protocol"
6F 74 6F 63 6F 6C
00 00 00 00 00 00 00 00                   <- reserved (8 bytes)
XX XX XX XX XX XX XX XX XX XX             <- info_hash (20 bytes)
XX XX XX XX XX XX XX XX XX XX
YY YY YY YY YY YY YY YY YY YY           <- peer_id (20 bytes)
YY YY YY YY YY YY YY YY YY YY
```

### 2.3 Handshake Procedure

1. **Initiator** sends their handshake immediately upon connection.
2. **Responder** reads the `info_hash` field (bytes 28-47).
   - If the responder is serving this torrent, it sends its own handshake back.
   - If not, it **MUST** close the connection.
3. **Initiator** validates the received `info_hash` matches what it sent.
   - Mismatch = close connection.
4. **Initiator** may also validate the `peer_id` against what the tracker reported.
   - Mismatch = close connection (some clients skip this check).

**Important:** The responder may wait to see the initiator's `info_hash` before sending anything. This lets a single listening port serve multiple torrents.

### 2.4 pstr Field

The `pstrlen`/`pstr` prefix exists so that different protocols can be trivially distinguished on the same port. For BitTorrent v1, `pstrlen` is always `0x13` (19) and `pstr` is always the ASCII bytes for `"BitTorrent protocol"`.

### 2.5 peer_id Conventions

The 20-byte `peer_id` identifies the client. Two common encoding schemes exist:

- **Azureus-style:** `-XX0000-` where XX is a two-letter client code and 0000 is a version number, followed by random bytes. Example: `-qB4530-` for qBittorrent 4.5.3.
- **Shadow-style:** A single letter client code, followed by version digits, followed by `---` padding and random bytes.

---

## 3. Reserved Bytes and Extension Signaling

The 8 reserved bytes (offsets 20-27 in the handshake) are a bitfield where specific bits signal support for protocol extensions. The base protocol sets all bits to zero.

### 3.1 Known Bit Assignments (BEP 4)

```
Byte  Bit (hex)  Extension                         BEP
----  ---------  --------------------------------  ----
  0     0x80     Azureus Messaging Protocol          -
  2     0x08     BitTorrent Location-aware Protocol   -
  5     0x10     Extension Protocol (LTEP)           10
  5     0x02     Extension Negotiation Protocol       -
  5     0x01     Extension Negotiation Protocol       -
  7     0x01     DHT (Distributed Hash Table)         5
  7     0x02     XBT Peer Exchange                    -
  7     0x04     Fast Extension                       6
  7     0x08     NAT Traversal                        -
  7     0x10     Hybrid torrent legacy-to-v2          -
```

### 3.2 How to Set/Check Bits

```
// Enable Extension Protocol (BEP 10)
reserved[5] |= 0x10;

// Enable Fast Extension (BEP 6)
reserved[7] |= 0x04;

// Enable DHT (BEP 5)
reserved[7] |= 0x01;

// Check if peer supports Extension Protocol
bool supportsLTEP = (reserved[5] & 0x10) != 0;

// Check if peer supports Fast Extension
bool supportsFast = (reserved[7] & 0x04) != 0;

// Check if peer supports DHT
bool supportsDHT = (reserved[7] & 0x01) != 0;
```

### 3.3 Future Extensions

BEP 4 recommends that new extensions use the Extension Protocol (BEP 10) instead of allocating new reserved bits. This avoids bit collisions since BEP 10 uses dynamically negotiated message IDs.

---

## 4. Message Framing

After the handshake, all communication uses length-prefixed messages.

### 4.1 General Message Format

```
Offset  Size      Field          Description
------  --------  -----------    ---------------------------------
0       4         length         Total bytes following this field
4       1         message_id     Message type identifier
5       variable  payload        Message-specific data
```

- `length` is a 4-byte big-endian unsigned integer
- `length` includes the `message_id` byte plus the `payload`
- A message with `length = 0` is a **keep-alive** (no `message_id` or `payload`)
- A message with `length = 1` has a `message_id` but no payload (e.g., choke)

### 4.2 Example: Choke Message (length=1, id=0)

```
00 00 00 01  <- length = 1
00           <- message_id = 0 (choke)
```

### 4.3 Example: Have Message (length=5, id=4, piece index=42)

```
00 00 00 05  <- length = 5
04           <- message_id = 4 (have)
00 00 00 2A  <- piece_index = 42
```

### 4.4 Maximum Message Size

The protocol does not define a maximum message size. In practice, `piece` messages are the largest, bounded by the block size requested. Clients historically reject requests larger than 128 KB (2^17 bytes), so the practical maximum message size is:

```
4 (length prefix) + 1 (id) + 4 (index) + 4 (begin) + 131072 (block) = 131,085 bytes
```

Most implementations use 16 KB (2^14) blocks, making the typical maximum:

```
4 + 1 + 4 + 4 + 16384 = 16,397 bytes
```

---

## 5. Base Protocol Messages (BEP 3)

### 5.0 Keep-Alive

```
Length:     0
Message ID: (none)
Payload:    (none)
Wire bytes: 00 00 00 00
```

Keep-alive messages have a length prefix of zero and contain nothing else. They carry no semantic meaning - their sole purpose is to prevent the connection from being closed due to inactivity. See [Section 11](#11-keep-alive) for timing details.

---

### 5.1 choke (ID = 0)

```
Length:     1
Message ID: 0
Payload:    (none)
Wire bytes: 00 00 00 01 00
```

Notifies the remote peer that it is being choked. No data requests from the remote peer will be answered until an `unchoke` is sent.

**Behavioral impact (base protocol - without BEP 6):**
- The remote peer MUST consider all of its pending (unanswered) requests to be **discarded**.
- The remote peer SHOULD NOT send new `request` messages while choked.

**Behavioral impact (with BEP 6 Fast Extension):**
- Pending requests are NOT implicitly discarded. Each will receive either a `piece` response or an explicit `reject` message.
- Requests for pieces in the Allowed Fast set may still be honored.

---

### 5.2 unchoke (ID = 1)

```
Length:     1
Message ID: 1
Payload:    (none)
Wire bytes: 00 00 00 01 01
```

Notifies the remote peer that it is no longer choked. The remote peer may now send `request` messages.

---

### 5.3 interested (ID = 2)

```
Length:     1
Message ID: 2
Payload:    (none)
Wire bytes: 00 00 00 01 02
```

Informs the remote peer that this client wants pieces it has. A peer MUST be interested before it can be unchoked (unchoke slots are only given to interested peers).

Clients should keep this state up-to-date at all times, even while choked.

---

### 5.4 not interested (ID = 3)

```
Length:     1
Message ID: 3
Payload:    (none)
Wire bytes: 00 00 00 01 03
```

Informs the remote peer that this client does not want any pieces it has. This allows the remote peer to free its unchoke slot.

---

### 5.5 have (ID = 4)

```
Length:     5
Message ID: 4
Payload:    4-byte big-endian piece index
Wire bytes: 00 00 00 05 04 [XX XX XX XX]
```

```
Offset  Size  Field        Description
------  ----  -----------  ---------------------------
0       4     length       Always 5
4       1     id           Always 4
5       4     piece_index  Zero-based index of the piece
```

Sent when a peer finishes downloading and verifying a piece. It announces to all connected peers that this piece is now available.

**Implementation notes:**
- Update the local record of the remote peer's piece availability.
- Recalculate `am_interested` - if this peer now has a piece we need, send `interested`.
- Used for rarest-first bookkeeping (increment the availability count for this piece).

---

### 5.6 bitfield (ID = 5)

```
Length:     1 + ceil(num_pieces / 8)
Message ID: 5
Payload:    Variable-length bitfield
Wire bytes: 00 00 00 [LL] 05 [bitfield bytes...]
```

```
Offset  Size              Field     Description
------  ----------------  --------  ---------------------------------
0       4                 length    1 + ceil(num_pieces / 8)
4       1                 id        Always 5
5       ceil(pieces / 8)  bitfield  One bit per piece
```

**Timing rule:** The `bitfield` message may ONLY be sent as the **first message** after the handshake, before any other messages. If a client has no pieces, it may omit the `bitfield` entirely.

**Bit encoding:**

```
Byte 0:  [ piece_0 | piece_1 | piece_2 | piece_3 | piece_4 | piece_5 | piece_6 | piece_7 ]
           bit 7     bit 6     bit 5     bit 4     bit 3     bit 2     bit 1     bit 0

Byte 1:  [ piece_8 | piece_9 | piece_10 | ... ]
```

- The **high bit** (bit 7, MSB) of byte 0 corresponds to piece index 0
- The **low bit** (bit 0, LSB) of byte 0 corresponds to piece index 7
- A set bit (1) means the peer **has** the piece
- A cleared bit (0) means the peer **does not have** the piece
- Spare bits in the last byte (when `num_pieces` is not a multiple of 8) MUST be zero

**Example:** A peer that has pieces 0, 2, 4, 8, and 10 out of 12 total pieces:

```
Piece indices:  0  1  2  3  4  5  6  7 | 8  9 10 11 |  (spare)
Bit values:     1  0  1  0  1  0  0  0 | 1  0  1  0 |  0 0 0 0
Byte values:    0xA8                    | 0xA0       |
```

Wire bytes: `00 00 00 03 05 A8 A0`

**Error conditions:**
- A bitfield of the wrong length - close the connection.
- Any spare bits set to 1 - close the connection.

---

### 5.7 request (ID = 6)

```
Length:     13
Message ID: 6
Payload:    index (4) + begin (4) + length (4)
Wire bytes: 00 00 00 0D 06 [XX XX XX XX] [YY YY YY YY] [ZZ ZZ ZZ ZZ]
```

```
Offset  Size  Field    Description
------  ----  -------  -------------------------------------------
0       4     length   Always 13 (0x0D)
4       1     id       Always 6
5       4     index    Zero-based piece index
9       4     begin    Byte offset within the piece
13      4     length   Number of bytes requested (the block size)
```

**Block size conventions:**
- Standard block size: **16,384 bytes** (2^14 = 16 KB)
- The last block in a piece may be smaller (truncated by piece boundary)
- Historical maximum: 2^17 (128 KB) - clients may close connections requesting more
- Some older clients used 2^15 (32 KB) blocks

**Rules:**
- MUST NOT send `request` while the peer is choking you (base protocol)
- With BEP 6: requests for Allowed Fast pieces may be sent while choked
- Clients should pipeline multiple requests (see [Section 9](#9-request-pipelining))

---

### 5.8 piece (ID = 7)

```
Length:     9 + block_size
Message ID: 7
Payload:    index (4) + begin (4) + block (variable)
Wire bytes: 00 00 00 [LL] 07 [XX XX XX XX] [YY YY YY YY] [data...]
```

```
Offset  Size       Field   Description
------  ---------  ------  -----------------------------------
0       4          length  9 + block_size
4       1          id      Always 7
5       4          index   Zero-based piece index
9       4          begin   Byte offset within the piece
13      variable   block   The raw piece data
```

This is the actual data transfer message. After receiving all blocks of a piece, the client MUST verify the piece by computing its SHA-1 hash and comparing it to the hash from the torrent metainfo.

- If the hash matches: announce the piece to all peers via `have` messages.
- If the hash fails: discard the piece and re-request it (possibly from different peers).

---

### 5.9 cancel (ID = 8)

```
Length:     13
Message ID: 8
Payload:    index (4) + begin (4) + length (4)
Wire bytes: 00 00 00 0D 08 [XX XX XX XX] [YY YY YY YY] [ZZ ZZ ZZ ZZ]
```

```
Offset  Size  Field    Description
------  ----  -------  -------------------------------------------
0       4     length   Always 13 (0x0D)
4       1     id       Always 8
5       4     index    Zero-based piece index
9       4     begin    Byte offset within the piece
13      4     length   Block size (must match the original request)
```

Cancels a previously sent `request`. The payload MUST exactly match a previously sent `request` message. Primarily used during endgame mode (see [Section 10](#10-endgame-mode)).

A peer receiving a `cancel` SHOULD make a best-effort attempt to not send the corresponding `piece` message, but it is not required to succeed (the data may already be in the send buffer).

---

### 5.10 port (ID = 9)

```
Length:     3
Message ID: 9
Payload:    2-byte big-endian port number
Wire bytes: 00 00 00 03 09 [PP PP]
```

```
Offset  Size  Field   Description
------  ----  ------  ------------------------------------------
0       4     length  Always 3
4       1     id      Always 9
5       2     port    UDP port for this peer's DHT node
```

Defined by BEP 5. Sent by peers that support DHT to indicate the UDP port their DHT node listens on. Upon receiving this message, the peer should add the sender to its DHT routing table.

This message is sent after the handshake when the peer's reserved bytes indicate DHT support (`reserved[7] & 0x01`).

---

## 6. Connection State Machine

### 6.1 Per-Connection State

Each connection maintains **four boolean flags** - two describing the local client's state, two describing the remote peer's state:

```
Flag              Initial Value  Meaning
----------------  -------------  ----------------------------------------
am_choking        true           I am choking the remote peer
am_interested     false          I am interested in the remote peer's data
peer_choking      true           The remote peer is choking me
peer_interested   false          The remote peer is interested in my data
```

### 6.2 State Transitions

```
Message Received      State Change
--------------------  -----------------------------------------
choke                 peer_choking = true
unchoke               peer_choking = false
interested            peer_interested = true
not interested        peer_interested = false
```

```
Message Sent          State Change
--------------------  -----------------------------------------
choke                 am_choking = true
unchoke               am_choking = false
interested            am_interested = true
not interested        am_interested = false
```

### 6.3 Data Transfer Conditions

Data transfer requires two conditions to align:

```
Download is possible when:  am_interested = true  AND  peer_choking = false
Upload is possible when:    peer_interested = true AND  am_choking = false
```

### 6.4 Connection Lifecycle State Machine

```
                    +------------------+
                    |   TCP Connected  |
                    +--------+---------+
                             |
                    Send/Receive Handshake
                             |
                    +--------v---------+
                    | Handshake        |
                    | Validation       |
                    +--------+---------+
                             |
              info_hash mismatch? ---> Close Connection
                             |
                    +--------v---------+
                    | Post-Handshake   |  (bitfield may be sent here)
                    | (optional        |  (BEP 10 extended handshake here)
                    |  bitfield)       |  (BEP 6 have_all/have_none here)
                    +--------+---------+
                             |
                    +--------v---------+
                    | Active           |  Normal message exchange
                    | (choked,         |  interested/not interested
                    |  not interested) |  have, request, piece, cancel
                    +--------+---------+
                             |
                    Connection error, timeout, or protocol violation
                             |
                    +--------v---------+
                    |   Closed         |
                    +------------------+
```

### 6.5 Message Ordering Rules

1. The **handshake** is always first (both directions).
2. The **bitfield** (if sent) MUST be the first message after the handshake.
   - With BEP 6: `have_all` or `have_none` may replace bitfield (same timing rule).
3. The **BEP 10 extended handshake** should be sent immediately after the standard handshake (and bitfield, if sent) when both peers advertise BEP 10 support.
4. All other messages may be sent in any order after the above.

### 6.6 Interested State Management

A client MUST keep its `interested` state accurate at all times:

- Set `interested` when the peer has at least one piece you need
- Set `not interested` when the peer has no pieces you need
- Update after every `have` message, `bitfield`, or when you complete a piece
- Keep updating even while choked - the peer uses your interest state for unchoke decisions

---

## 7. Choking Algorithm

The choking algorithm determines which peers receive upload bandwidth. It is not part of the wire protocol itself, but it is essential for correct behavior. BEP 3 defines the reference algorithm.

### 7.1 Leecher Choking (Downloading)

When the client is downloading (does not have all pieces):

1. **Rechoke interval:** Every **10 seconds**, re-evaluate which peers to unchoke.
2. **Top 4 selection:** Unchoke the **4 peers** with the best **download rate** to you that are **interested**. These are the "regular unchoke" slots.
3. **Optimistic unchoke:** In addition to the top 4, maintain **1 peer** that is unchoked regardless of download rate (if interested, it counts toward the 4 allowed downloaders). This peer rotates every **30 seconds**.
4. **New connection bonus:** Newly connected peers are **3x more likely** to be selected as the optimistic unchoke. This gives them a chance to prove their upload rate.
5. **Uninterested fast peers:** If a peer has a better upload rate than the top 4 but is not interested, unchoke it anyway. If it becomes interested, choke the worst of the current top 4.

### 7.2 Seeder Choking (Upload Only)

When the client has all pieces (is a seed):

- Use the peer's **upload rate** (how fast they download from you) instead of download rate to rank peers.
- Otherwise the same algorithm applies: top 4 + 1 optimistic unchoke.

### 7.3 Anti-Snubbing

If a peer has not sent any piece data to you for **more than 60 seconds** while you are downloading from it, consider it "snubbed":

- Do not upload to a snubbed peer except as an optimistic unchoke.
- This prevents wasting upload bandwidth on peers that are not reciprocating.

### 7.4 Summary

```
Role       Ranking Metric    Regular Slots  Optimistic  Rechoke   Rotate
---------  ----------------  -------------  ----------  --------  --------
Leecher    Download rate     4              1           10 sec    30 sec
Seeder     Upload rate       4              1           10 sec    30 sec
```

---

## 8. Piece Selection

### 8.1 Strict Priority (Partially Downloaded Pieces)

**Rule:** Once a piece has been partially downloaded (at least one block received), it takes **highest priority**. All remaining blocks of that piece should be requested before starting any new piece.

**Rationale:** A partially downloaded piece provides zero value until all blocks are received and the hash is verified. Completing it as fast as possible converts wasted bandwidth into a usable piece.

### 8.2 Rarest First

For selecting which **new** piece to start downloading:

1. Track the **availability count** for each piece - how many connected peers have it.
2. Initialize from `bitfield` messages; update on each `have` message received.
3. Decrement when a peer disconnects.
4. Select pieces with the **lowest availability** count first.
5. Among pieces with equal availability, **randomize** the selection. This prevents many clients from all requesting the same rare piece simultaneously.

**Rationale:** Rarest-first ensures that rare pieces get more copies in the swarm quickly, preventing the situation where a seeder leaves and a piece becomes unavailable.

### 8.3 Random First Piece

When a client first joins a swarm and has **no pieces at all**, it should select its first piece **randomly** rather than rarest-first.

**Rationale:** A new peer needs to get a complete piece as quickly as possible so it has something to upload. Rare pieces may only be available from slow peers. A random piece is more likely to be available from a fast peer.

After obtaining the first 1-4 pieces, switch to rarest-first.

### 8.4 Piece Selection Summary

```
Priority  Strategy             When
--------  -------------------  ----------------------------------------
1         Strict priority      Piece is partially downloaded
2         Random first piece   Client has no complete pieces yet
3         Rarest first         Normal operation (randomize among ties)
4         Endgame mode         Near completion (see Section 10)
```

---

## 9. Request Pipelining

### 9.1 Why Pipelining Matters

Without pipelining, each block transfer requires a full round trip:

```
Client                  Peer
  |--- request #1 ------->|
  |                        | (peer reads disk, prepares data)
  |<------ piece #1 -------|
  |--- request #2 ------->|    <- RTT wasted here
  |                        |
  |<------ piece #2 -------|
```

With pipelining, multiple requests are in flight simultaneously:

```
Client                  Peer
  |--- request #1 ------->|
  |--- request #2 ------->|
  |--- request #3 ------->|
  |<------ piece #1 -------|
  |--- request #4 ------->|    <- new request sent as each piece arrives
  |<------ piece #2 -------|
  |--- request #5 ------->|
  |<------ piece #3 -------|
```

This is described in BEP 3 as "the most crucial performance item."

### 9.2 Queue Depth Guidelines

| Scenario                          | Recommended Queue Depth |
|-----------------------------------|-------------------------|
| 5 Mbps link, 50ms latency         | 10 requests             |
| High bandwidth (100+ Mbps)        | 50-250 requests         |
| Low bandwidth / high latency      | 5-10 requests           |
| BEP 10 `reqq` peer advertisement  | Use peer's value        |

The BEP 10 extended handshake includes a `reqq` field where a peer can advertise how many outstanding requests it supports. The default value in libtorrent is **250**.

### 9.3 Implementation Strategy

A simple and effective approach:

1. Maintain a queue of pending (sent but unanswered) requests per peer.
2. When the queue drops below the target depth, send more requests.
3. When a `piece` response arrives, remove the corresponding request from the queue.
4. When `choke` is received (base protocol), clear the entire queue.
5. When `choke` is received (BEP 6), wait for `reject` or `piece` for each pending request.

### 9.4 Block Size

The standard block size is **16,384 bytes** (16 KB). This is not mandated by the protocol, but is a strong convention:

- All modern clients use 16 KB blocks
- Clients may close connections that request blocks larger than 128 KB
- The last block of a piece may be smaller than 16 KB
- Block requests MUST NOT span piece boundaries

---

## 10. Endgame Mode

### 10.1 Problem

Near the end of a download, the last few blocks tend to trickle in slowly. If the peers holding those blocks are slow or congested, the download stalls even though plenty of bandwidth is available from other peers.

### 10.2 Entry Condition

There is no single mandated threshold. Common implementations:

- Enter when **all remaining blocks have been requested** (every missing block has at least one pending request)
- Enter when the number of remaining blocks is less than the number of outstanding requests, and no more than ~20 blocks remain
- Enter when all pieces have been requested (even if not all blocks have been)

### 10.3 Behavior

1. Send `request` messages for **every missing block** to **every peer** that has the corresponding piece and is not choking you.
2. When a block arrives from any peer, immediately send `cancel` messages to **all other peers** that were also asked for that block.
3. Exit endgame mode when the download completes or when enough blocks arrive to leave the threshold.

### 10.4 Efficiency Considerations

- Endgame mode generates duplicate requests, which wastes some bandwidth.
- The `cancel` messages limit the waste, but there is a window where the data is already in flight.
- Because endgame mode only activates for the last few blocks, the total waste is small.
- Randomize which blocks are requested from which peers to reduce duplicates.

---

## 11. Keep-Alive

### 11.1 Format

```
Wire bytes: 00 00 00 00
```

A keep-alive is simply a message with `length = 0`. No message ID, no payload.

### 11.2 Timing

- Peers SHOULD send a keep-alive if no other message has been sent for **2 minutes** (120 seconds).
- Peers MAY close the connection if no message (including keep-alive) is received for a timeout period. Common timeout values are 2-3 minutes.
- Implementations should track the last message sent and send a keep-alive before the peer's timeout would trigger.

### 11.3 Recommendations

- Send keep-alives at a shorter interval than the expected timeout (e.g., send every 90 seconds if the timeout is 120 seconds).
- The keep-alive interval may be shorter when data transfer is expected but not happening (possible network issue).
- Any message resets the keep-alive timer - if you send a `have` or `request`, there is no need to also send a keep-alive.

---

## 12. Fast Extension (BEP 6)

### 12.1 Activation

Both peers MUST set the Fast Extension bit in their handshake reserved bytes:

```
reserved[7] |= 0x04;
```

The extension is only active if **both** sides set this bit. If only one side sets it, both MUST fall back to base protocol behavior.

### 12.2 New Messages

BEP 6 adds five new message types:

#### 12.2.1 Have All (ID = 0x0E / 14)

```
Length:     1
Message ID: 0x0E
Payload:    (none)
Wire bytes: 00 00 00 01 0E
```

Equivalent to a `bitfield` with all bits set. Indicates the sender has all pieces (is a seed). Subject to the same timing rules as `bitfield` - MUST be the first message after the handshake and MUST NOT be sent alongside a `bitfield`.

#### 12.2.2 Have None (ID = 0x0F / 15)

```
Length:     1
Message ID: 0x0F
Payload:    (none)
Wire bytes: 00 00 00 01 0F
```

Equivalent to a `bitfield` with all bits cleared. Indicates the sender has no pieces. Subject to the same timing rules as `bitfield`.

#### 12.2.3 Suggest Piece (ID = 0x0D / 13)

```
Length:     5
Message ID: 0x0D
Payload:    4-byte big-endian piece index
Wire bytes: 00 00 00 05 0D [XX XX XX XX]
```

```
Offset  Size  Field        Description
------  ----  -----------  ----------------------------------
0       4     length       Always 5
4       1     id           Always 0x0D (13)
5       4     piece_index  Suggested piece index
```

An advisory message suggesting the peer download a particular piece. The peer may ignore this suggestion. Commonly used when a peer has a piece cached in memory and can serve it quickly.

#### 12.2.4 Reject Request (ID = 0x10 / 16)

```
Length:     13
Message ID: 0x10
Payload:    index (4) + begin (4) + length (4)
Wire bytes: 00 00 00 0D 10 [XX XX XX XX] [YY YY YY YY] [ZZ ZZ ZZ ZZ]
```

```
Offset  Size  Field    Description
------  ----  -------  -------------------------------------------
0       4     length   Always 13 (0x0D)
4       1     id       Always 0x10 (16)
5       4     index    Piece index from the rejected request
9       4     begin    Byte offset from the rejected request
13      4     length   Block size from the rejected request
```

Notifies the peer that a previously sent `request` will not be fulfilled. The payload MUST exactly match a previously received `request`.

**Critical behavioral guarantee:** With BEP 6, every `request` message is guaranteed to result in **exactly one** response - either a `piece` message or a `reject` message. This is the fundamental change from the base protocol.

#### 12.2.5 Allowed Fast (ID = 0x11 / 17)

```
Length:     5
Message ID: 0x11
Payload:    4-byte big-endian piece index
Wire bytes: 00 00 00 05 11 [XX XX XX XX]
```

```
Offset  Size  Field        Description
------  ----  -----------  -------------------------------------------
0       4     length       Always 5
4       1     id           Always 0x11 (17)
5       4     piece_index  Index of the allowed fast piece
```

Indicates that the receiver may request this piece even while choked. Multiple `allowed_fast` messages build up the peer's Allowed Fast set.

**Rules:**
- Senders MAY list pieces they do not yet possess (receivers MUST NOT interpret this as a `have`)
- Senders SHOULD send Allowed Fast messages to newly connecting peers
- The set is typically generated algorithmically (see Section 12.4)
- Requests for Allowed Fast pieces from choked peers SHOULD be honored
- A peer MAY reject an Allowed Fast request if it lacks resources, the piece was already sent, or the requester has exceeded `k` pieces

### 12.3 Changed Semantics

BEP 6 changes the behavior of `choke`:

| Behavior                         | Base Protocol         | With BEP 6                          |
|----------------------------------|-----------------------|-------------------------------------|
| Choke discards pending requests  | Yes (implicit)        | No - each gets piece or reject      |
| Can request while choked         | No                    | Yes, for Allowed Fast pieces        |
| Unrequested piece received       | Ignore (no rule)      | MUST close connection               |
| Request-response guarantee       | No                    | Yes - exactly one response per request |

### 12.4 Allowed Fast Set Generation Algorithm

The Allowed Fast set is generated deterministically from the peer's IP address and the torrent's infohash. This means both sides can compute the same set independently.

**Parameters:**
- `k` - number of pieces in the set (default: **10**)
- `sz` - total number of pieces in the torrent
- `infohash` - 20-byte SHA-1 hash of the torrent's info dictionary
- `ip` - peer's IPv4 address (as observed externally)

**Algorithm (pseudocode):**

```
function generate_allowed_fast_set(k, sz, infohash, ip):
    allowed_set = []

    // Step 1: Mask IP to /24 subnet (prevents gaming with multiple IPs)
    x = bytes(ip & 0xFFFFFF00)          // 4 bytes, network byte order

    // Step 2: Append infohash
    x = x + infohash                     // now 24 bytes

    // Step 3: Iterate with SHA-1
    while len(allowed_set) < k:
        x = SHA1(x)                      // x is now 20 bytes (the hash output)

        // Step 4: Extract up to 5 piece indices from the 20-byte hash
        for i in 0..4:
            if len(allowed_set) >= k:
                break

            // Extract 4-byte big-endian integer at offset i*4
            y = uint32_big_endian(x[i*4 .. i*4+4])

            // Convert to piece index
            index = y % sz

            // Only add if not already in the set
            if index not in allowed_set:
                allowed_set.append(index)

    return allowed_set
```

**C++ reference implementation (from BEP 6):**

```cpp
void generate_fast_set(
    uint32 k,               // number of pieces in set
    uint32 sz,              // number of pieces in torrent
    const char infohash[20],// infohash of torrent
    uint32 ip,              // in host byte order (localhost = 0x7f000001)
    std::vector<uint32> &a) // output: generated piece indices
{
    a.clear();
    std::string x;
    char buf[4];
    *(uint32*)buf = htonl(ip & 0xffffff00);
    x.assign(buf, 4);
    x.append(infohash, 20);
    while (a.size() < k) {
        x = SHA1(x);
        for (int i = 0; i < 5 && a.size() < k; i++) {
            int j = i * 4;
            uint32 y = ntohl(*(uint32*)(x.data() + j));
            uint32 index = y % sz;
            if (std::find(a.begin(), a.end(), index) == a.end()) {
                a.push_back(index);
            }
        }
    }
}
```

**Test vector:**

For a torrent with **1313 pieces**, infohash = `0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa` (20 bytes of 0xAA), and peer IP **80.4.4.200**:

| Set Size (k) | Allowed Fast Pieces                           |
|--------------|------------------------------------------------|
| 7            | 1059, 431, 808, 1217, 287, 376, 1188           |
| 9            | 1059, 431, 808, 1217, 287, 376, 1188, 353, 508 |

---

## 13. Extension Protocol (BEP 10)

### 13.1 Activation

Both peers MUST set the Extension Protocol bit in their handshake reserved bytes:

```
reserved[5] |= 0x10;
```

Check: `(reserved[5] & 0x10) != 0`

This is bit 20 from the right (counting from 0), which is bit 4 of byte 5.

### 13.2 Extended Message Format

All extension protocol messages use message ID **20** (0x14):

```
Offset  Size      Field              Description
------  --------  -----------------  ----------------------------------
0       4         length             Total message length (after this)
4       1         message_id         Always 20 (0x14)
5       1         extended_msg_id    0 = handshake, 1+ = extension msg
6       variable  payload            Bencoded dictionary or extension data
```

### 13.3 Extended Handshake (extended_msg_id = 0)

The extended handshake MUST be sent immediately after the standard BitTorrent handshake (and bitfield, if sent) when both peers support BEP 10.

**Wire format:**

```
[4-byte length] [0x14] [0x00] [bencoded dictionary]
```

The payload is a **bencoded dictionary** containing extension negotiation data. All keys are optional. Unknown keys MUST be ignored. All keys are case-sensitive.

### 13.4 Handshake Dictionary Keys

#### 13.4.1 `m` - Extension Message ID Map (Required for usefulness)

A dictionary mapping extension names to numeric message IDs. These IDs are **local to the sending peer** - each peer assigns its own IDs.

```
"m" => {
    "ut_metadata" => 1,
    "ut_pex"      => 2,
    "lt_donthave" => 3
}
```

**Meaning:** "When you want to send me a `ut_metadata` message, use extended message ID 1. For `ut_pex`, use ID 2."

**Critical detail:** Extension IDs MUST be stored **per-peer** because every peer may assign different IDs for the same extension.

**Disabling extensions:** Setting an extension's ID to **0** disables it:

```
"m" => { "ut_metadata" => 0 }   // disables ut_metadata
```

**ID uniqueness:** Each ID within a peer's `m` dictionary must be unique (two extensions cannot share the same ID).

#### 13.4.2 `p` - Listen Port

```
"p" => 6881
```

Integer. The TCP port this peer is listening on for incoming connections. Useful for peers behind NAT or those that changed ports.

#### 13.4.3 `v` - Client Version String

```
"v" => "uTorrent 1.2"
```

UTF-8 string. Human-readable client name and version.

#### 13.4.4 `yourip` - Your External IP

```
"yourip" => <4 or 16 raw bytes>
```

A byte string containing the **compact representation** of the IP address the sender sees the receiver connecting from. This helps peers discover their external IP.

- 4 bytes for IPv4
- 16 bytes for IPv6
- No port information is included

#### 13.4.5 `ipv4` - Sender's IPv4 Address

```
"ipv4" => <4 raw bytes>
```

Compact 4-byte IPv4 address of the sender.

#### 13.4.6 `ipv6` - Sender's IPv6 Address

```
"ipv6" => <16 raw bytes>
```

Compact 16-byte IPv6 address of the sender.

#### 13.4.7 `reqq` - Request Queue Depth

```
"reqq" => 250
```

Integer. The number of outstanding `request` messages this peer supports without dropping any. Senders should not exceed this when pipelining requests.

Default value (if not specified): **250** (libtorrent default).

#### 13.4.8 `metadata_size` - Torrent Metadata Size

```
"metadata_size" => 31235
```

Integer. The total size of the torrent's info dictionary in bytes. Used by BEP 9 (Metadata Extension) for magnet link resolution. Not defined in BEP 10 itself but commonly included.

### 13.5 Example Extended Handshake

**Bencoded payload:**

```
d
  1:md
    11:ut_metadatai1e
    6:ut_pexi2e
  e
  1:pi6881e
  1:v13:uTorrent 1.2
  5:yourip4:<4 bytes>
  4:reqqi250e
e
```

**As wire bytes (conceptual):**

```
[4-byte length] [0x14] [0x00] [bencoded dictionary above]
```

### 13.6 Subsequent Extended Messages

After the handshake, extension messages are sent using the message IDs negotiated in the `m` dictionary:

```
[4-byte length] [0x14] [extended_msg_id] [extension-specific payload]
```

For example, to send a `ut_pex` message to a peer that assigned ID 2 to `ut_pex`:

```
[4-byte length] [0x14] [0x02] [PEX payload...]
```

### 13.7 Multiple Handshakes

The extended handshake may be sent **more than once** during a connection's lifetime. Clients MUST NOT disconnect a peer for sending multiple handshakes.

When a subsequent handshake is received:
- The `m` dictionary is **additive** - it only needs to contain changes
- Extensions not mentioned in the update retain their previous IDs
- Setting an extension to 0 disables it
- Other dictionary keys are replaced with the new values

### 13.8 Extension Naming Convention

Extension names SHOULD be prefixed with a two-character (or one-character) client code to avoid collisions:

- `ut_` - uTorrent extensions (e.g., `ut_metadata`, `ut_pex`)
- `lt_` - libtorrent extensions (e.g., `lt_donthave`, `lt_tex`)

All one-byte and two-byte extension names are reserved for the specification.

### 13.9 Common Extensions Negotiated via BEP 10

| Extension Name  | BEP  | Purpose                              |
|-----------------|------|--------------------------------------|
| `ut_metadata`   | 9    | Metadata exchange (magnet links)     |
| `ut_pex`        | 11   | Peer Exchange                        |
| `lt_donthave`   | 54   | Signal piece loss (for streaming)    |
| `upload_only`   | 21   | Signal upload-only mode              |
| `lt_tex`        | 27   | Private torrents tracker exchange    |
| `ut_holepunch`  | 55   | NAT hole punching                    |

---

## 14. Error Handling and Disconnection

### 14.1 Mandatory Disconnection Conditions

A client MUST close the connection when:

| Condition                                                | Source     |
|----------------------------------------------------------|------------|
| Handshake `info_hash` does not match any served torrent  | BEP 3      |
| Handshake `peer_id` does not match tracker-reported ID   | BEP 3 (*)  |
| Bitfield has wrong length                                | BEP 3      |
| Bitfield has spare bits set to 1                         | BEP 3      |
| Unsolicited `piece` received (never requested)           | BEP 6      |
| `reject` received for a request never sent               | BEP 6      |

(*) Some clients skip peer_id validation.

### 14.2 Recommended Disconnection Conditions

A client SHOULD close the connection when:

| Condition                                                | Source      |
|----------------------------------------------------------|-------------|
| No messages received for 2+ minutes (timeout)            | BEP 3       |
| Request for block larger than 128 KB (2^17 bytes)        | Convention  |
| Excessive requests from a choked peer                    | BEP 6       |
| Unknown message ID received (optional - may also ignore) | Convention  |
| Repeated protocol violations                             | Convention  |
| Peer sends `have_all`/`have_none` after first message    | BEP 6       |

### 14.3 Handling Unknown Messages

The recommended approach for forward compatibility:

1. Read the length prefix to determine message size.
2. If the message ID is unknown, skip `length - 1` bytes of payload.
3. Continue processing subsequent messages.

This allows newer protocol extensions to coexist with older clients.

### 14.4 Hash Failures

When a downloaded piece fails SHA-1 verification:

- Discard the entire piece data.
- The piece must be re-requested (possibly from different peers).
- Optionally, track which peers contributed bad data and penalize them.
- Repeated hash failures from the same peer suggest a malicious or buggy peer - consider disconnecting.

---

## 15. Super Seeding

Super seeding (BEP 16) is an optimization for initial seeders. It is mentioned here for completeness since it affects wire-level message patterns.

### 15.1 Behavior

A super-seeding client:

1. Advertises itself as having **no pieces** (sends empty bitfield or `have_none`).
2. Sends `have` messages one at a time, only for pieces that are **rare** in the swarm or that have **never been sent** to any peer.
3. After sending a `have` for piece X, waits until it observes another peer also sending `have` for piece X before advertising a new piece.
4. This ensures each piece gets distributed widely before the next piece is revealed.

### 15.2 Efficiency

Super seeding reportedly reduces the upload required from approximately 150-200% of the torrent size down to approximately 105%.

### 15.3 Limitations

- Not recommended for general use - only useful for the initial seeder.
- Reduces piece selection flexibility for peers.
- May slow down the swarm if the seeder's algorithm makes poor choices.

---

## 16. Complete Message ID Reference

```
ID    Hex   Name              BEP   Length    Payload
----  ----  ----------------  ----  --------  ---------------------------------
-     -     keep-alive        3     0         (none)
0     0x00  choke             3     1         (none)
1     0x01  unchoke           3     1         (none)
2     0x02  interested        3     1         (none)
3     0x03  not interested    3     1         (none)
4     0x04  have              3     5         piece_index(4)
5     0x05  bitfield          3     1+X       bitfield(X)
6     0x06  request           3     13        index(4) + begin(4) + length(4)
7     0x07  piece             3     9+X       index(4) + begin(4) + block(X)
8     0x08  cancel            3     13        index(4) + begin(4) + length(4)
9     0x09  port              5     3         port(2)
13    0x0D  suggest piece     6     5         piece_index(4)
14    0x0E  have all          6     1         (none)
15    0x0F  have none         6     1         (none)
16    0x10  reject request    6     13        index(4) + begin(4) + length(4)
17    0x11  allowed fast      6     5         piece_index(4)
20    0x14  extended          10    2+X       ext_id(1) + payload(X)
```

**Note:** Message IDs 10-12 and 18-19 are not assigned by any accepted BEP. ID 20 is the extension protocol container - the actual extension message type is determined by the `extended_msg_id` byte within the payload.

---

## 17. Implementation Notes

### 17.1 Byte Order

All multi-byte integers in the wire protocol are **big-endian** (network byte order). This applies to:

- The 4-byte message length prefix
- The 4-byte piece index in `have`, `request`, `piece`, `cancel`, `suggest`, `reject`, `allowed_fast`
- The 4-byte begin and length fields in `request`, `piece`, `cancel`, `reject`
- The 2-byte port in the `port` message

### 17.2 Connection Limits

Typical implementations maintain:

- 50-200 peer connections per torrent
- Maximum upload slots: 4 regular + 1 optimistic = 5 concurrent uploads
- Maximum connections total: 200-500 across all torrents

### 17.3 Typical Message Sequence

A typical connection between two leechers:

```
Initiator                                Responder
    |                                        |
    |------ handshake --->                   |
    |                     <--- handshake ----|
    |------ bitfield --->                    |
    |                     <--- bitfield -----|
    |                                        |
    |  (Both sides evaluate interest)        |
    |                                        |
    |------ interested -->                   |
    |                     <--- interested ---|
    |                                        |
    |  (Choking algorithm runs)              |
    |                                        |
    |                     <--- unchoke ------|
    |------ request ----->                   |
    |------ request ----->                   |
    |------ request ----->                   |
    |                     <--- piece --------|
    |------ request ----->                   |
    |                     <--- piece --------|
    |                     <--- piece --------|
    |                                        |
    |  (Piece complete, hash verified)       |
    |                                        |
    |------ have -------->                   |
    |                                        |
```

### 17.4 Typical Connection with BEP 6 + BEP 10

```
Initiator                                Responder
    |                                        |
    |------ handshake --->                   |
    |  (reserved[5] |= 0x10, [7] |= 0x04)   |
    |                     <--- handshake ----|
    |                                        |
    |------ have_all ---->                   |  (BEP 6: replaces bitfield)
    |                     <--- have_none ----|  (BEP 6: no pieces)
    |                                        |
    |------ ext handshake -->                |  (BEP 10: msg 20, ext_id 0)
    |                     <--- ext handshake-|
    |                                        |
    |------ allowed_fast -->                 |  (BEP 6: pieces 45, 102, ...)
    |------ allowed_fast -->                 |
    |------ allowed_fast -->                 |
    |                                        |
    |                     <--- interested ---|
    |                                        |
    |  (Peer is choked, but has allowed      |
    |   fast pieces, so it can request them) |
    |                                        |
    |                     <--- request ------|  (for allowed fast piece)
    |------ piece ------->                   |
    |                                        |
    |  (Choking algorithm unchokes peer)     |
    |                                        |
    |------ unchoke ----->                   |
    |                     <--- request ------|
    |                     <--- request ------|
    |------ piece ------->                   |
    |------ piece ------->                   |
    |                                        |
    |  (Choking algorithm re-chokes peer)    |
    |                                        |
    |------ choke ------->                   |
    |------ reject ------>                   |  (BEP 6: explicit reject)
    |------ reject ------>                   |  (for each pending request)
    |                                        |
```

### 17.5 WebRTC/WebTorrent Considerations

When using WebRTC data channels as the transport (WebTorrent):

- The handshake and all messages are identical at the byte level.
- WebRTC data channels are reliable and ordered (when configured as such), making them equivalent to TCP for protocol purposes.
- The `port` message (DHT) is typically not relevant for browser peers.
- Extension protocol negotiation works the same way.
- Browser peers cannot accept incoming TCP connections, so they rely on WebRTC signaling (trackers or DHT) for connectivity.

### 17.6 Handling Piece Boundaries

When requesting blocks:

```
piece_length    = torrent's piece size (from metainfo, e.g., 262144 = 256 KB)
block_size      = 16384 (16 KB, standard)
total_size      = torrent's total file size

// Blocks per piece (all pieces except possibly the last)
blocks_per_piece = ceil(piece_length / block_size)

// Last piece may be shorter
last_piece_length = total_size - (num_pieces - 1) * piece_length

// Last block of any piece may be shorter
for each piece p:
    this_piece_length = (p == num_pieces - 1) ? last_piece_length : piece_length
    for block_offset = 0; block_offset < this_piece_length; block_offset += block_size:
        this_block_size = min(block_size, this_piece_length - block_offset)
        // request(p, block_offset, this_block_size)
```

### 17.7 Bitfield Encoding Example

For a torrent with 20 pieces where the client has pieces 0, 3, 5, 12, 18:

```
Piece indices:   0  1  2  3  4  5  6  7 | 8  9 10 11 12 13 14 15 | 16 17 18 19 | spare
Bit values:      1  0  0  1  0  1  0  0 | 0  0  0  0  1  0  0  0 | 0  0  1  0  | 0 0 0 0
                 ^MSB                LSB^ ^MSB                LSB^ ^MSB     LSB^
Byte values:     0x94                    | 0x08                    | 0x20       |

Bitfield bytes:  94 08 20  (3 bytes = ceil(20/8))
```

Wire message: `00 00 00 04 05 94 08 20`

(length = 4 = 1 byte msg_id + 3 bytes bitfield)

---

## Appendix A: Bencoding Quick Reference

BEP 10 extended handshakes use bencoding. Quick reference:

| Type       | Format                    | Example                |
|------------|---------------------------|------------------------|
| Integer    | `i<number>e`              | `i42e` = 42           |
| String     | `<length>:<data>`         | `4:spam` = "spam"     |
| List       | `l<items>e`               | `li1ei2ee` = [1, 2]   |
| Dictionary | `d<key><value>...e`       | `d3:fooi1ee` = {foo:1} |

Dictionary keys MUST be byte strings and MUST appear in sorted order.

---

## Appendix B: Reserved Byte Layout Diagram

```
Byte:  [  0  ] [  1  ] [  2  ] [  3  ] [  4  ] [  5  ] [  6  ] [  7  ]
Bits:  76543210 76543210 76543210 76543210 76543210 76543210 76543210 76543210

Assigned bits:
Byte 0, bit 7 (0x80): Azureus Messaging Protocol
Byte 2, bit 3 (0x08): BitTorrent Location-aware Protocol
Byte 5, bit 4 (0x10): Extension Protocol (BEP 10)
Byte 5, bit 1 (0x02): Extension Negotiation Protocol
Byte 5, bit 0 (0x01): Extension Negotiation Protocol
Byte 7, bit 0 (0x01): DHT (BEP 5)
Byte 7, bit 1 (0x02): XBT Peer Exchange
Byte 7, bit 2 (0x04): Fast Extension (BEP 6)
Byte 7, bit 3 (0x08): NAT Traversal
Byte 7, bit 4 (0x10): Hybrid torrent legacy-to-v2 upgrade
```

All other bits are reserved and MUST be set to zero.

---

## Appendix C: Message Size Summary

For quick reference during implementation, here are the exact byte counts of each complete message (including the 4-byte length prefix):

| Message          | Total Bytes on Wire      | Formula                   |
|------------------|--------------------------|---------------------------|
| keep-alive       | 4                        | 4                         |
| choke            | 5                        | 4 + 1                     |
| unchoke          | 5                        | 4 + 1                     |
| interested       | 5                        | 4 + 1                     |
| not interested   | 5                        | 4 + 1                     |
| have             | 9                        | 4 + 1 + 4                 |
| bitfield         | 5 + ceil(pieces/8)       | 4 + 1 + ceil(pieces/8)    |
| request          | 17                       | 4 + 1 + 4 + 4 + 4         |
| piece            | 13 + block_size          | 4 + 1 + 4 + 4 + block     |
| cancel           | 17                       | 4 + 1 + 4 + 4 + 4         |
| port             | 7                        | 4 + 1 + 2                 |
| suggest piece    | 9                        | 4 + 1 + 4                 |
| have all         | 5                        | 4 + 1                     |
| have none        | 5                        | 4 + 1                     |
| reject request   | 17                       | 4 + 1 + 4 + 4 + 4         |
| allowed fast     | 9                        | 4 + 1 + 4                 |
| extended         | 6 + payload_size         | 4 + 1 + 1 + payload       |
