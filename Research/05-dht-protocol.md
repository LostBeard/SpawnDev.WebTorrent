# DHT Protocol - BEP 5, BEP 44, BEP 46

Comprehensive reference for the BitTorrent DHT (Distributed Hash Table) protocol and its extensions for arbitrary data storage and mutable torrent updates. Covers the three BEPs that together enable fully decentralized peer discovery, key-value storage, and self-updating torrents.

## Table of Contents

1. [BEP 5 - DHT Protocol (Kademlia)](#bep-5---dht-protocol-kademlia)
2. [BEP 44 - Storing Arbitrary Data in the DHT](#bep-44---storing-arbitrary-data-in-the-dht)
3. [BEP 46 - Updating Torrents via DHT Mutable Items](#bep-46---updating-torrents-via-dht-mutable-items)
4. [Security Considerations](#security-considerations)
5. [SpawnDev.WebTorrent Implementation Notes](#spawndevwebtorrent-implementation-notes)

---

## BEP 5 - DHT Protocol (Kademlia)

**Spec:** http://bittorrent.org/beps/bep_0005.html
**Status:** Accepted
**Authors:** Andrew Loewenstern, Arvid Norberg
**Transport:** UDP

### Overview

BitTorrent uses a "distributed sloppy hash table" (DHT) for storing peer contact information for "trackerless" torrents. Each peer becomes a tracker. The protocol is based on Kademlia and is implemented over UDP.

**Terminology:**
- **Peer** - A client/server listening on a TCP port implementing the BitTorrent transfer protocol
- **Node** - A client/server listening on a UDP port implementing the DHT protocol
- **Routing Table** - A data structure storing known good nodes, organized by distance from the local node

### Node IDs and Distance Metric

Each node has a globally unique 160-bit identifier (node ID), chosen at random from the same space as BitTorrent infohashes (SHA-1 output).

**Distance metric:** XOR, interpreted as an unsigned integer.

```
distance(A, B) = |A XOR B|
```

Smaller values mean "closer." This is a metric in the mathematical sense:
- `distance(A, A) = 0`
- `distance(A, B) > 0` when `A != B`
- `distance(A, B) = distance(B, A)` (symmetric)
- `distance(A, B) + distance(B, C) >= distance(A, C)` (triangle inequality)

**Example:**

```
Node A: 0xABCD...   (160 bits)
Node B: 0xAB00...   (160 bits)
XOR:    0x00CD...   (small - these nodes are "close")

Node C: 0x5432...   (160 bits)
A XOR C: 0xFFFF... (large - these nodes are "far")
```

### Routing Table Structure

The routing table covers the entire 160-bit ID space (0 to 2^160). It is subdivided into **buckets**, each covering a portion of the space.

**Bucket rules:**
- An empty table starts with one bucket covering the full range [0, 2^160)
- Each bucket holds at most **K = 8** nodes
- When a full bucket's range includes the node's own ID, it splits into two buckets at the midpoint
- Buckets that don't include the node's own ID never split - new nodes are simply discarded

**Node states:**
| State | Condition |
|-------|-----------|
| Good | Responded to our query in the last 15 minutes, OR has ever responded and sent us a query within 15 minutes |
| Questionable | No activity for 15 minutes |
| Bad | Failed to respond to multiple consecutive queries |

**Insertion rules for full buckets:**
1. If any node is **bad**, replace it with the new node
2. If any node is **questionable**, ping the least-recently-seen questionable node
3. If the pinged node responds, try the next questionable node
4. If a pinged node fails, replace it with the new node
5. If all nodes are good, discard the new node (unless the bucket can split)

**Bucket refresh:** Buckets not changed in 15 minutes should be refreshed by picking a random ID in the bucket's range and performing a `find_node` lookup for it.

### Bootstrap Process

1. Start with known bootstrap nodes (from torrent file `nodes` key, or well-known routers)
2. Issue `find_node` queries for the node's own ID to the closest known nodes
3. Iteratively query closer and closer nodes until no closer nodes are found
4. This populates the routing table with nodes near the local node's ID
5. The routing table should be saved between sessions

**Torrent file `nodes` key:**

```
nodes = [["127.0.0.1", 6881], ["your.router.node", 4804]]
```

This replaces the `announce` key for trackerless torrents.

### KRPC Protocol

KRPC (Kademlia RPC) is the message layer - bencoded dictionaries sent over UDP. Single request, single response, no retry at the protocol level.

**Common fields in every message:**

| Key | Type | Description |
|-----|------|-------------|
| `t` | string | Transaction ID (2 bytes typical, echoed in response) |
| `y` | string | Message type: `"q"` = query, `"r"` = response, `"e"` = error |
| `v` | string | Client version (optional, 4 bytes: 2-char client ID + 2-char version per BEP 20) |

#### Contact Encoding

**Compact peer info** (6 bytes):

```
[4 bytes: IPv4 address, network byte order][2 bytes: port, network byte order]
```

Example: `192.168.1.1:6881` = `0xC0A80101 0x1AE1`

**Compact node info** (26 bytes):

```
[20 bytes: node ID][4 bytes: IPv4 address][2 bytes: port]
```

Multiple nodes are concatenated into a single string (e.g., 8 nodes = 208 bytes).

#### Query Format

```
{
    "t": "<transaction-id>",
    "y": "q",
    "q": "<method-name>",
    "a": { <arguments> }
}
```

#### Response Format

```
{
    "t": "<transaction-id>",
    "y": "r",
    "r": { <return-values> }
}
```

#### Error Format

```
{
    "t": "<transaction-id>",
    "y": "e",
    "e": [<error-code>, "<error-message>"]
}
```

**Error codes:**

| Code | Description |
|------|-------------|
| 201 | Generic Error |
| 202 | Server Error |
| 203 | Protocol Error (malformed packet, invalid arguments, bad token) |
| 204 | Method Unknown |

### DHT Query: ping

The simplest query. Verifies a node is alive.

**Arguments:**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Querying node's ID |

**Response:**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Responding node's ID |

**Example - Query:**

```
Logical:  {"t":"aa", "y":"q", "q":"ping", "a":{"id":"abcdefghij0123456789"}}
Bencoded: d1:ad2:id20:abcdefghij0123456789e1:q4:ping1:t2:aa1:y1:qe
```

**Example - Response:**

```
Logical:  {"t":"aa", "y":"r", "r":{"id":"mnopqrstuvwxyz123456"}}
Bencoded: d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re
```

### DHT Query: find_node

Find the contact information for a specific node by its ID.

**Arguments:**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Querying node's ID |
| `target` | 20-byte string | ID of the node being sought |

**Response:**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Responding node's ID |
| `nodes` | string | Compact node info for the target or the K (8) closest good nodes |

**Example - Query:**

```
Logical:  {"t":"aa", "y":"q", "q":"find_node", "a":{"id":"abcdefghij0123456789", "target":"mnopqrstuvwxyz123456"}}
Bencoded: d1:ad2:id20:abcdefghij01234567896:target20:mnopqrstuvwxyz123456e1:q9:find_node1:t2:aa1:y1:qe
```

**Example - Response:**

```
Logical:  {"t":"aa", "y":"r", "r":{"id":"0123456789abcdefghij", "nodes":"def456..."}}
Bencoded: d1:rd2:id20:0123456789abcdefghij5:nodes9:def456...e1:t2:aa1:y1:re
```

### DHT Query: get_peers

Find peers downloading a specific torrent (by infohash).

**Arguments:**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Querying node's ID |
| `info_hash` | 20-byte string | Infohash of the target torrent |

**Response (has peers):**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Responding node's ID |
| `token` | string | Opaque write token for future `announce_peer` |
| `values` | list of strings | Each string is 6 bytes of compact peer info |

**Response (no peers, closest nodes instead):**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Responding node's ID |
| `token` | string | Opaque write token |
| `nodes` | string | Compact node info for the K closest nodes to the infohash |

**Token management:** The token is an opaque value that must be presented in a subsequent `announce_peer` to prove the querier recently performed a `get_peers`. The reference implementation uses SHA-1 of the querier's IP concatenated with a secret that rotates every 5 minutes. Tokens up to 10 minutes old are accepted.

**Example - Query:**

```
Logical:  {"t":"aa", "y":"q", "q":"get_peers", "a":{"id":"abcdefghij0123456789", "info_hash":"mnopqrstuvwxyz123456"}}
Bencoded: d1:ad2:id20:abcdefghij01234567899:info_hash20:mnopqrstuvwxyz123456e1:q9:get_peers1:t2:aa1:y1:qe
```

**Example - Response with peers:**

```
Logical:  {"t":"aa", "y":"r", "r":{"id":"abcdefghij0123456789", "token":"aoeusnth", "values":["axje.u", "idhtnm"]}}
Bencoded: d1:rd2:id20:abcdefghij01234567895:token8:aoeusnth6:valuesl6:axje.u6:idhtnmee1:t2:aa1:y1:re
```

Each value in the list (e.g. `"axje.u"`) is 6 bytes: 4-byte IP + 2-byte port.

**Example - Response with closest nodes:**

```
Logical:  {"t":"aa", "y":"r", "r":{"id":"abcdefghij0123456789", "token":"aoeusnth", "nodes":"def456..."}}
Bencoded: d1:rd2:id20:abcdefghij01234567895:nodes9:def456...5:token8:aoeusnthe1:t2:aa1:y1:re
```

### DHT Query: announce_peer

Announce that the peer (controlling this node) is downloading a torrent.

**Arguments:**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Querying node's ID |
| `info_hash` | 20-byte string | Infohash of the torrent being downloaded |
| `port` | integer | Port the peer is listening on |
| `token` | string | Token received from a recent `get_peers` response |
| `implied_port` | integer (0 or 1) | Optional. If 1, ignore `port` and use the UDP source port instead |

The `implied_port` flag is useful for peers behind NAT that don't know their external port, especially when using uTP (which shares the DHT UDP port).

**Response:**

| Key | Type | Description |
|-----|------|-------------|
| `id` | 20-byte string | Responding node's ID |

**Example - Query:**

```
Logical:  {"t":"aa", "y":"q", "q":"announce_peer", "a":{"id":"abcdefghij0123456789",
           "implied_port":1, "info_hash":"mnopqrstuvwxyz123456", "port":6881, "token":"aoeusnth"}}
Bencoded: d1:ad2:id20:abcdefghij012345678912:implied_porti1e9:info_hash20:
           mnopqrstuvwxyz1234564:porti6881e5:token8:aoeusnthe1:q13:announce_peer1:t2:aa1:y1:qe
```

**Example - Response:**

```
Logical:  {"t":"aa", "y":"r", "r":{"id":"mnopqrstuvwxyz123456"}}
Bencoded: d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re
```

### Iterative Lookup Algorithm

The core Kademlia lookup used by `get_peers` and `find_node`:

1. Select the alpha (typically 3) closest nodes to the target from the routing table
2. Send parallel queries to those alpha nodes
3. From responses, collect returned nodes
4. Select the alpha closest unqueried nodes from the combined set
5. Repeat until no closer nodes are found (or peers are found for `get_peers`)
6. For `get_peers`: announce to the K closest nodes that returned tokens

The lookup converges in O(log N) steps, where N is the number of nodes in the DHT.

### BitTorrent Protocol Extension

Peers supporting DHT set bit 0 of byte 7 in the 8-byte reserved field of the BT handshake:

```
reserved[7] |= 0x01   // DHT support flag
```

After the handshake, a peer that supports DHT sends a PORT message (message ID 0x09) with a 2-byte payload containing the UDP port of its DHT node (network byte order). The receiving peer should attempt to ping this node.

### Error Example

```
Logical:  {"t":"aa", "y":"e", "e":[201, "A Generic Error Ocurred"]}
Bencoded: d1:eli201e23:A Generic Error Ocurrede1:t2:aa1:y1:ee
```

---

## BEP 44 - Storing Arbitrary Data in the DHT

**Spec:** http://bittorrent.org/beps/bep_0044.html
**Status:** Draft
**Authors:** Arvid Norberg, Steven Siloti
**Depends on:** BEP 5

### Overview

BEP 44 extends the DHT from a peer-location store to a general-purpose key-value store. It supports two types of items:

| Type | Key derivation | Authentication | Updatable |
|------|---------------|----------------|-----------|
| **Immutable** | SHA-1 hash of the bencoded value | None needed (content-addressed) | No |
| **Mutable** | SHA-1 hash of public key (+ optional salt) | Ed25519 signature | Yes (monotonic seq) |

**Value size limit:** 1000 bytes (bencoded form). Storing nodes MAY reject values exceeding this.

**Value type:** Any bencoded type - string, integer, list, or dictionary.

### New Messages: get and put

These parallel the existing `get_peers` and `announce_peer`:

| BEP 5 | BEP 44 | Purpose |
|--------|--------|---------|
| `get_peers` | `get` | Retrieve data by target hash |
| `announce_peer` | `put` | Store data at target hash |

Both `get` responses include `nodes`/`nodes6` and a `token`, with the same semantics as `get_peers`.

### Immutable Items

Stored under the SHA-1 hash of their bencoded value. Since they can't be modified, no authentication is needed. The requesting node verifies the data by hashing it and comparing with the target.

#### Immutable put

**Request:**

```
{
    "a": {
        "id": <20 byte node ID>,
        "token": <write token from get>,
        "v": <any bencoded value, encoded size <= 1000>
    },
    "t": <transaction-id>,
    "y": "q",
    "q": "put"
}
```

**Response:**

```
{
    "r": { "id": <20 byte node ID> },
    "t": <transaction-id>,
    "y": "r"
}
```

#### Immutable get

**Request:**

```
{
    "a": {
        "id": <20 byte node ID>,
        "target": <20 byte SHA-1 hash of the bencoded value>
    },
    "t": <transaction-id>,
    "y": "q",
    "q": "get"
}
```

**Response:**

```
{
    "r": {
        "id": <20 byte node ID>,
        "token": <write token>,
        "v": <the stored value, whose SHA-1 matches target>,
        "nodes": <compact IPv4 node info close to target>,
        "nodes6": <compact IPv6 node info close to target>
    },
    "t": <transaction-id>,
    "y": "r"
}
```

### Mutable Items

Stored under the SHA-1 hash of the Ed25519 public key (optionally concatenated with a salt). Only the holder of the corresponding private key can update the value.

**Key fields for mutable items:**

| Field | Type | Size | Description |
|-------|------|------|-------------|
| `k` | string | 32 bytes | Ed25519 public key |
| `seq` | integer | up to 8 bytes | Monotonically increasing sequence number |
| `sig` | string | 64 bytes | Ed25519 signature |
| `salt` | string | 0-64 bytes | Optional - allows one key to publish multiple independent items |
| `cas` | integer | up to 8 bytes | Optional - compare-and-swap for conflict resolution |
| `v` | any bencoded | <= 1000 bytes | The stored value |

#### Target ID Calculation

Without salt:

```
target = SHA-1(k)
```

With salt:

```
target = SHA-1(k + salt)
```

Where `k` is the 32-byte public key and `salt` is the raw salt bytes (not bencoded).

#### Signature Construction

The signature covers a carefully constructed buffer to prevent parser attacks:

**Without salt:**

```
3:seqi<seq>e1:v<len>:<value>
```

**With salt:**

```
4:salt<salt_len>:<salt>3:seqi<seq>e1:v<len>:<value>
```

**Step by step:**
1. Bencode the sequence number: `3:seqi<N>e`
2. Bencode the value key prefix: `1:v`
3. Bencode the value itself (e.g. `12:Hello World!`)
4. If salt is present and non-empty, prepend: `4:salt<len>:<salt>`
5. Concatenate all parts into a single buffer
6. Sign (or verify) this buffer with Ed25519

**Example without salt:**

```
Value: "Hello World!" (12 bytes)
Sequence: 1
Buffer to sign: "3:seqi1e1:v12:Hello World!"
```

**Example with salt "foobar":**

```
Value: "Hello World!" (12 bytes)
Sequence: 4
Buffer to sign: "4:salt6:foobar3:seqi4e1:v12:Hello World!"
```

**Why this format?** By using bencoded key-value pairs in a fixed order, it prevents attacks where a malicious node tricks a parser into interpreting part of the length as the sequence number, or vice versa. The concatenation is deterministic regardless of bencoding serialization order.

#### Mutable put

**Request:**

```
{
    "a": {
        "cas": <optional: expected current seq number>,
        "id": <20 byte node ID>,
        "k": <32 byte Ed25519 public key>,
        "salt": <optional: salt string, max 64 bytes>,
        "seq": <monotonically increasing integer>,
        "sig": <64 byte Ed25519 signature>,
        "token": <write token from get>,
        "v": <any bencoded value, encoded size <= 1000>
    },
    "t": <transaction-id>,
    "y": "q",
    "q": "put"
}
```

**Storage node rules:**
- MUST verify the Ed25519 signature before accepting
- MUST reject if `seq` is lower than what's already stored
- If `seq` is equal and value is the same, SHOULD reset the expiration timer
- If `seq` is equal and value differs, MUST reject
- The target hash is implied by `k` (and `salt` if present) - it is NOT in the message

**Response (success):**

```
{
    "r": { "id": <20 byte node ID> },
    "t": <transaction-id>,
    "y": "r"
}
```

#### Compare-and-Swap (CAS)

The `cas` field prevents race conditions when multiple nodes write to the same key.

**How it works:**
1. Node A does a `get`, receives value with `seq = 5`
2. Node A creates a new value with `seq = 6` and sets `cas = 5`
3. The storage node checks: does current `seq` == `cas` (5)?
4. If yes, the put succeeds
5. If no (another writer already bumped to seq 6), the put fails with error 301

**Rules:**
- `cas` only applies to mutable puts
- If no current value exists, `cas` SHOULD be ignored
- When sending a `put` to a node that returned no data for `get`, `cas` SHOULD NOT be included
- On mismatch, storage node MUST return error 301

#### Mutable get

**Request:**

```
{
    "a": {
        "id": <20 byte node ID>,
        "seq": <optional: minimum sequence number>,
        "target": <20 byte SHA-1 of (public key + salt)>
    },
    "t": <transaction-id>,
    "y": "q",
    "q": "get"
}
```

The optional `seq` field is an optimization: if the storage node's value has a sequence number <= the requested `seq`, it omits `k`, `v`, and `sig` from the response (saving bandwidth when the requester already has the latest version).

**Response:**

```
{
    "r": {
        "id": <20 byte node ID>,
        "k": <32 byte Ed25519 public key>,
        "nodes": <compact IPv4 node info>,
        "nodes6": <compact IPv6 node info>,
        "seq": <sequence number>,
        "sig": <64 byte Ed25519 signature>,
        "token": <write token>,
        "v": <the stored value>
    },
    "t": <transaction-id>,
    "y": "r"
}
```

### BEP 44 Error Codes

In addition to BEP 5 error codes (201-204):

| Code | Description |
|------|-------------|
| 205 | Message (`v` field) too big |
| 206 | Invalid signature |
| 207 | Salt (`salt` field) too big |
| 301 | CAS hash mismatched - re-read and retry |
| 302 | Sequence number less than current |

Error 301 is critical for multi-writer synchronization. Implementations MUST emit it on CAS mismatch.

### Expiration and Republishing

- Items MAY expire after **2 hours** without re-announcement
- Items SHOULD be re-announced **once per hour** to stay alive
- Any node can re-announce a mutable item (just repeat the signature - no private key needed)
- This allows data to survive after the original publisher goes offline

**Republish optimization:** Subscribers can skip republishing if ALL of these are true during a get lookup:
1. They find more than 8 copies of the data
2. The 8 nodes closest to the target all have the data
3. For mutable items, only nodes with the most recent sequence number count

### Test Vectors

#### Test 1: Mutable (no salt)

```
Value (bencoded): 12:Hello World!
Buffer to sign:   3:seqi1e1:v12:Hello World!

Public key:  77ff84905a91936367c01360803104f92432fcd904a43511876df5cdf3e7e548
Private key: e06d3183d14159228433ed599221b80bd0a5ce8352e4bdf0262f76786ef1c74d
             b7e7a9fea2c0eb269d61e3b38e450a22e754941ac78479d6c54e1faf6037881d

Target ID:   4a533d47ec9c7d95b1ad75f576cffc641853b750

Signature:   305ac8aeb6c9c151fa120f120ea2cfb923564e11552d06a5d856091e5e853cff
             1260d3f39e4999684aa92eb73ffd136e6f4f3ecbfda0ce53a1608ecd7ae21f01
```

#### Test 2: Mutable (with salt "foobar")

```
Value (bencoded): 12:Hello World!
Salt:             foobar
Buffer to sign:   4:salt6:foobar3:seqi1e1:v12:Hello World!

Public key:  77ff84905a91936367c01360803104f92432fcd904a43511876df5cdf3e7e548
Private key: e06d3183d14159228433ed599221b80bd0a5ce8352e4bdf0262f76786ef1c74d
             b7e7a9fea2c0eb269d61e3b38e450a22e754941ac78479d6c54e1faf6037881d

Target ID:   411eba73b6f087ca51a3795d9c8c938d365e32c1

Signature:   6834284b6b24c3204eb2fea824d82f88883a3d95e8b4a21b8c0ded553d17d17d
             df9a8a7104b1258f30bed3787e6cb896fca78c58f8e03b5f18f14951a87d9a08
```

#### Test 3: Immutable

```
Value (bencoded): 12:Hello World!

Target ID:   e5f96f6f38320f0f33959cb4d3d656452117aadb
```

---

## BEP 46 - Updating Torrents via DHT Mutable Items

**Spec:** http://bittorrent.org/beps/bep_0046.html
**Status:** Draft
**Author:** Luca Matteis
**Depends on:** BEP 44

### Overview

BEP 46 uses BEP 44's mutable items to create self-updating torrent pointers. A publisher stores a signed infohash in the DHT under their public key. When they update the content, they publish a new infohash with an incremented sequence number. Subscribers poll the DHT and automatically switch to the new torrent.

This is the decentralized alternative to BEP 39 (Updating Torrents Via Feed URL), which requires an HTTP server.

### Publishing Flow

1. Publisher generates an Ed25519 key pair
2. Publisher creates a torrent for their content, obtaining an infohash
3. Publisher stores a BEP 44 mutable item:
   - `k` = their 32-byte public key
   - `v` = `{"ih": "<20-byte infohash>"}` (bencoded dictionary)
   - `seq` = monotonically increasing sequence number
   - `sig` = Ed25519 signature of the concatenated seq + v buffer
4. The item is stored at `target = SHA-1(public_key)` (or `SHA-1(public_key + salt)` with salt)

**Value format:**

```
{
    "v": {
        "ih": "<20 byte infohash of the target torrent>"
    }
}
```

Bencoded: `d2:ih20:<infohash bytes>e`

### Update Flow

1. Publisher creates a new torrent with updated content
2. Publisher increments `seq`
3. Publisher signs the new (seq, v) pair
4. Publisher issues `put` requests to the K closest nodes to the target

Storage nodes reject the put if the new `seq` is <= the currently stored `seq`, preventing rollback attacks.

### Subscribing Flow

1. Consumer knows the publisher's public key (from a magnet link, out-of-band, etc.)
2. Consumer calculates `target = SHA-1(public_key)` (or `SHA-1(public_key + salt)`)
3. Consumer issues `get` requests to nodes near the target
4. Consumer verifies:
   - The returned `k` hashes to the expected target
   - The Ed25519 signature is valid
5. Consumer extracts the infohash from `v["ih"]`
6. Consumer starts downloading the torrent
7. Consumer periodically re-polls to check for updates (passing `seq` to avoid redundant data transfer)

### Magnet Link Format

```
magnet:?xs=urn:btpk:<public-key-hex>&s=<salt-hex>
```

- `xs` = "exact source" (NOT `xt`)
- `s` = salt (optional, hex-encoded)

**Example (no salt):**

```
magnet:?xs=urn:btpk:8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e
```

**Example (with salt "n"):**

```
magnet:?xs=urn:btpk:8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e&s=6e
```

### Keeping Items Alive

Both publishers and subscribers should periodically re-put the mutable item to prevent expiration. Subscribers can do this without the private key - they just replay the `k`, `seq`, `sig`, and `v` they received.

### Test Vectors

#### Test 1: Magnet to target ID (no salt)

```
Public key (hex): 8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e
Target ID (hex):  cc3f9d90b572172053626f9980ce261a850d050b
```

Verification: `SHA-1(0x8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e) = cc3f9d90b572172053626f9980ce261a850d050b`

#### Test 2: Magnet to target ID (with salt)

```
Public key (hex): 8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e
Salt (hex):       6e  (ASCII: "n")
Target ID (hex):  59ee7c2cb9b4f7eb1986ee2d18fd2fdb8a56554f
```

Verification: `SHA-1(0x8543d3e6...36ed63e + 0x6e) = 59ee7c2cb9b4f7eb1986ee2d18fd2fdb8a56554f`

---

## Security Considerations

### BEP 5 - DHT

**Sybil attacks:** An attacker can generate node IDs close to a target infohash and intercept `get_peers`/`announce_peer` traffic. Mitigations include BEP 42 (DHT Security Extension) which ties node IDs to IP addresses.

**Eclipse attacks:** An attacker fills a victim's routing table with malicious nodes, isolating them from the real DHT. The bucket splitting rules and preference for long-running nodes provide some resistance.

**Token replay:** Tokens are tied to IP addresses and rotate every 5 minutes (accepted for 10 minutes), limiting the window for token abuse.

**Amplification:** UDP-based protocols are susceptible to amplification attacks. DHT responses should not be significantly larger than requests.

### BEP 44 - DHT Storage

**Value tampering (immutable):** Impossible - the target is the SHA-1 of the value. Any modification changes the hash.

**Value tampering (mutable):** Impossible without the private key - the Ed25519 signature covers the value and sequence number.

**Replay attacks (mutable):** Mitigated by the monotonic sequence number. Storage nodes reject any `put` with seq <= current. However, an attacker who captured a legitimately signed older value cannot use it to overwrite a newer one.

**Denial of service:** An attacker could flood storage nodes with `put` requests. The 1000-byte value limit and token requirement provide some protection.

**Key rotation:** BEP 44 does not define a key rotation mechanism. If a private key is compromised, the attacker can publish to that key permanently. Applications should consider key rotation strategies at a higher level.

### BEP 46 - Mutable Torrents

**Publisher impersonation:** Impossible without the Ed25519 private key.

**Infohash substitution:** An attacker could try to point subscribers to malicious content by publishing a different infohash. This fails because they can't sign with the publisher's key.

**Stale data:** If the publisher stops refreshing, the mutable item expires in ~2 hours. Subscribers can keep it alive, but if all subscribers go offline, the pointer is lost.

**Man-in-the-middle:** The signature verification prevents MITM modification of the infohash. However, the initial distribution of the public key (e.g., via a web page) must be done securely.

---

## SpawnDev.WebTorrent Implementation Notes

### Platform Support

| Feature | Desktop | Browser |
|---------|---------|---------|
| BEP 5 (DHT) | Full UDP-based Kademlia | Not available (no UDP sockets) |
| BEP 44 (DHT storage) | Full DHT put/get | Via WebSocket tracker relay (AgentChannel) |
| BEP 46 (Mutable torrents) | Full DHT mutable items | Via WebSocket tracker relay (AgentChannel) |

### Browser Workaround: AgentChannel

Browsers cannot run Kademlia because they have no UDP socket API. SpawnDev.WebTorrent works around this by relaying BEP 44/46 operations through the WebSocket tracker connection (AgentChannel). The signing, encoding, sequence handling, and verification logic is identical on both platforms - only the transport differs.

### Key Classes

| Class | File | Purpose |
|-------|------|---------|
| `DhtDiscovery` | DhtDiscovery.cs | Kademlia routing table, KRPC message handling, iterative lookups |
| `DhtMutableItems` | DhtMutableItems.cs | BEP 44/46 mutable item publish/subscribe, token caching, sequence persistence |
| `IDhtSigner` | IDhtSigner.cs | Pluggable signing interface (Ed25519Signer is the only implementation) |
| `AgentChannel` | AgentChannel.cs | High-level pub/sub that works on both platforms |

### Cryptographic Signing

All BEP 44/46 operations use Ed25519 exclusively, via `SpawnDev.BlazorJS.Cryptography` 3.1.0+:
- **Browser:** WebCrypto API (native, hardware-accelerated)
- **Desktop:** .NET `System.Security.Cryptography` Ed25519

Earlier versions used ECDSA-P256 but this has been fully replaced. Ed25519 is the only signing algorithm for all new SpawnDev code.

### Sequence Number Persistence

`DhtMutableItems` persists the last published sequence number to `AsyncFileSystem` at `webtorrent/_dht_seq/<public-key-hex>`. This prevents sequence number reuse after restarts, which would cause storage nodes to reject puts.
