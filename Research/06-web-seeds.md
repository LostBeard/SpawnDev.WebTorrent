# Web Seeds - BEP 17 and BEP 19

Comprehensive reference for HTTP-based seeding in BitTorrent. Covers both the Hoffman-style (BEP 17) and GetRight-style (BEP 19) web seed protocols, including their metadata formats, HTTP request mapping, multi-file handling, error recovery, and practical implementation considerations.

## Table of Contents

1. [Overview and Comparison](#overview-and-comparison)
2. [BEP 19 - WebSeed (GetRight-style)](#bep-19---webseed-getright-style)
3. [BEP 17 - HTTP Seeding (Hoffman-style)](#bep-17---http-seeding-hoffman-style)
4. [Multi-File Piece Boundary Handling](#multi-file-piece-boundary-handling)
5. [Error Handling and Recovery](#error-handling-and-recovery)
6. [Practical Implementation](#practical-implementation)
7. [SpawnDev.WebTorrent Implementation Notes](#spawndevwebtorrent-implementation-notes)

---

## Overview and Comparison

Both BEPs solve the same problem: using standard HTTP/FTP servers as permanent seeds for BitTorrent downloads. They differ in how they map BitTorrent pieces to HTTP requests.

| Feature | BEP 17 (Hoffman) | BEP 19 (GetRight) |
|---------|-------------------|--------------------|
| Metadata key | `httpseeds` | `url-list` |
| URL points to | Server-side script | Direct file URL |
| Server requirement | Custom script required | Standard HTTP server (no script) |
| Request format | `?info_hash=X&piece=N&ranges=...` | Standard HTTP Range header |
| Multi-file | Script handles piece-to-file mapping | Client handles piece-to-file mapping |
| Server changes needed | Yes (script) | None |
| Status | Draft | Accepted |
| Adoption | Limited | Very wide (Mainline, uTorrent, Azureus, libtorrent, WebTorrent, etc.) |

**BEP 19 is by far the more widely used protocol.** It requires no server-side changes - any standard HTTP server or CDN works. BEP 17 requires a custom server script that understands torrent metadata and piece-to-byte-range mapping.

---

## BEP 19 - WebSeed (GetRight-style)

**Spec:** http://bittorrent.org/beps/bep_0019.html
**Status:** Accepted
**Author:** Michael Burford (GetRight)

### Metadata

The `url-list` key is placed in the torrent file's top-level dictionary (NOT inside the `info` dict):

**Single URL:**

```
d
  8:announce27:http://tracker.com/announce
  8:url-list26:http://mirror.com/file.exe
  4:info...
e
```

**Multiple URLs:**

```
d
  8:announce27:http://tracker.com/announce
  8:url-listl
    26:http://mirror1.com/file.exe
    26:http://mirror2.com/file.exe
  e
  4:info...
e
```

### URL Construction

#### Single-File Torrents

If the URL ends with a `/`, the client appends the `name` field from the info dictionary:

| url-list value | Info name | Constructed URL |
|----------------|-----------|-----------------|
| `http://mirror.com/file.exe` | (ignored) | `http://mirror.com/file.exe` |
| `http://mirror.com/files/` | `file.exe` | `http://mirror.com/files/file.exe` |

#### Multi-File Torrents

For multi-file torrents, the `url-list` URL is a root folder. The client constructs the full URL by appending the torrent's `name` and then the file's `path` components:

```
URL = <url-list> + <name> + "/" + <path/file>
```

**Example:**

```
url-list: http://mirror.com/pub/
info.name: my-album
info.files[0].path: ["Track 01.mp3"]
info.files[1].path: ["covers", "front.jpg"]

Constructed URLs:
  http://mirror.com/pub/my-album/Track 01.mp3
  http://mirror.com/pub/my-album/covers/front.jpg
```

### HTTP Requests

BEP 19 uses standard HTTP byte-range requests. The client calculates which byte ranges of which files correspond to the needed pieces, then requests those ranges using the standard `Range` header.

#### Single-File Request

For a single-file torrent, the piece-to-byte mapping is straightforward:

```
Piece N starts at byte: N * piece_length
Piece N ends at byte:   min((N + 1) * piece_length - 1, total_length - 1)
```

**HTTP request for piece 5 of a torrent with 256KB pieces:**

```http
GET /file.exe HTTP/1.1
Host: mirror.com
Range: bytes=1310720-1572863
```

Where `1310720 = 5 * 262144` and `1572863 = 6 * 262144 - 1`.

#### Multi-File Request

For multi-file torrents, pieces can span file boundaries. When a piece spans multiple files, the client must issue separate HTTP requests to each file and assemble the responses. See [Multi-File Piece Boundary Handling](#multi-file-piece-boundary-handling) for details.

### Piece Selection for Web Seeds

BEP 19 recommends modifications to the standard "rarest first" piece selection to create large contiguous gaps for HTTP/FTP downloads to fill:

**Gap definition:** A gap is a sequence of consecutive missing pieces. Given bitfield `YYnnnnYnnY`, there are two gaps: one of 4 pieces and one of 2 pieces.

**Modified piece selection:**
1. When downloading from BitTorrent peers, prefer pieces from smaller gaps (filling them in from the high end)
2. This preserves large gaps for HTTP/FTP connections
3. HTTP/FTP connections start at the beginning of the largest gap and download sequentially

**"Pretty rare with biggest gap" algorithm:**

```
X = sqrt(peers) - 1
For each missing piece:
    If gap is bigger AND rareness is within (current_rarest + X): select it
    If gap is smaller: require rareness to be at least (current_rarest - X) lower
```

**Fill-in-the-gaps:** After 50% completion, randomly (1 in 10 pieces) pick the piece that's closest to a completed piece, ignoring rareness. This fills small gaps.

### SHA Verification

If a piece downloaded from an HTTP/FTP server fails SHA hash verification, the connection MUST be closed and that URL SHOULD be discarded permanently. Unlike BitTorrent peers (which might send bad data occasionally due to bugs), an HTTP server serving wrong data is fundamentally broken.

---

## BEP 17 - HTTP Seeding (Hoffman-style)

**Spec:** http://bittorrent.org/beps/bep_0017.html
**Status:** Draft
**Authors:** John Hoffman, DeHackEd

### Metadata

The `httpseeds` key is placed in the torrent file's top-level dictionary:

```
d["httpseeds"] = ["http://www.site1.com/seed.php", "http://www.site2.com/seed.php"]
```

### URL and Request Format

Unlike BEP 19, BEP 17 sends requests to a server-side script that interprets torrent piece numbers:

```
<url>?info_hash=<url-encoded hash>&piece=<piece number>&ranges=<start>-<end>,<start>-<end>
```

**Request for a full piece:**

```
http://www.site.com/seed.php?info_hash=%9C%D9i%8A%F5Uu%1A%91%86%AE%06lW%EA%21W%235%E0&piece=3
```

**Request for specific ranges within a piece:**

```
http://www.site.com/seed.php?info_hash=%9C%D9i%8A%F5Uu%1A%91%86%AE%06lW%EA%21W%235%E0&piece=8&ranges=49152-131071,180224-262143
```

**Key differences from BEP 19:**
- The `info_hash` is URL-encoded (raw 20 bytes, percent-encoded)
- The `piece` parameter is a piece index, not a byte range
- The `ranges` parameter specifies byte offsets within the piece (not within the file)
- The server script must understand torrent metadata to translate piece numbers to file byte ranges

### Response Format

| HTTP Status | Meaning |
|-------------|---------|
| 200 OK | Body contains the requested piece data (binary) |
| 503 Service Temporarily Unavailable | Body is an ASCII integer: seconds to wait before retrying |
| Any other | Error - client should back off and retry less frequently |

**When ranges are requested:** The response body is the concatenation of the requested ranges (in order), not the entire piece.

### Server-Side Requirements

The script must:
1. **Rate limit** uploads to prevent overwhelming the server
2. **Calculate retry delays** intelligently (telling peers how long to wait)
3. **Translate** piece numbers + info_hash to byte ranges within files

The script needs access to both the torrent's files AND the `.torrent` metadata to perform the piece-to-byte-range mapping.

**Optional but desirable features:**
- Ban peers that retry too frequently
- Monitor the tracker and stop seeding when enough P2P seeds exist
- Report back to the tracker as a seed

### Client-Side Behavior

The reference implementation:
1. Default retry time: **30 seconds**
2. After 3 failed retries: increase the delay with each cycle
3. Piece selection: request the rarest complete piece first; if all pieces are partially downloaded, skip one cycle, then request partials
4. On HTTP 503: set retry time to the value in the response body

---

## Multi-File Piece Boundary Handling

This is the trickiest part of web seed implementation - particularly for BEP 19, where the client must handle the piece-to-file mapping itself.

### The Problem

In multi-file torrents, the files are logically concatenated into a single byte stream. Pieces are fixed-size chunks of this stream. A single piece can span across two or more files.

**Example:**

```
Piece length: 256 KB (262,144 bytes)

File layout (concatenated):
  file1.txt:  400,000 bytes  (offsets 0 - 399,999)
  file2.txt:  300,000 bytes  (offsets 400,000 - 699,999)
  file3.txt:  200,000 bytes  (offsets 700,000 - 899,999)

Pieces:
  Piece 0: bytes 0 - 262,143        (entirely within file1.txt)
  Piece 1: bytes 262,144 - 524,287  (SPANS file1.txt and file2.txt)
           - file1.txt: bytes 262,144 - 399,999 (137,856 bytes)
           - file2.txt: bytes 0 - 124,287 (124,288 bytes)
  Piece 2: bytes 524,288 - 786,431  (SPANS file2.txt and file3.txt)
           - file2.txt: bytes 124,288 - 299,999 (175,712 bytes)
           - file3.txt: bytes 0 - 86,431 (86,432 bytes)
  Piece 3: bytes 786,432 - 899,999  (partial last piece, entirely in file3.txt)
           - file3.txt: bytes 86,432 - 199,999 (113,568 bytes)
```

### BEP 19 Multi-File Request Assembly

For piece 1 in the example above, the client issues TWO HTTP requests:

**Request 1 (to file1.txt):**

```http
GET /torrent-name/file1.txt HTTP/1.1
Host: mirror.com
Range: bytes=262144-399999
```

**Request 2 (to file2.txt):**

```http
GET /torrent-name/file2.txt HTTP/1.1
Host: mirror.com
Range: bytes=0-124287
```

The client concatenates the two response bodies to form the complete piece, then verifies the SHA hash.

### BEP 17 Multi-File Request

For BEP 17, the server script handles all of this. The client just requests:

```
http://seed.example.com/script.php?info_hash=...&piece=1
```

The script translates piece 1 into the appropriate file reads and returns the assembled piece data.

### Algorithm for Piece-to-File Mapping

```
function GetFileRanges(pieceIndex, pieceLength, files):
    pieceStart = pieceIndex * pieceLength
    pieceEnd = min(pieceStart + pieceLength, totalLength) - 1
    
    ranges = []
    fileOffset = 0
    
    for each file in files:
        fileStart = fileOffset
        fileEnd = fileOffset + file.length - 1
        
        if fileEnd < pieceStart:
            fileOffset += file.length
            continue  // file is entirely before this piece
        
        if fileStart > pieceEnd:
            break     // file is entirely after this piece
        
        // This file overlaps with the piece
        rangeStart = max(pieceStart, fileStart) - fileStart
        rangeEnd = min(pieceEnd, fileEnd) - fileStart
        
        ranges.append({
            file: file,
            offset: rangeStart,
            length: rangeEnd - rangeStart + 1
        })
        
        fileOffset += file.length
    
    return ranges
```

---

## Error Handling and Recovery

### BEP 19 Error Handling

| HTTP Status | Action |
|-------------|--------|
| 200 OK | Success - verify piece hash |
| 206 Partial Content | Success - the expected response for Range requests |
| 416 Range Not Satisfiable | Range is out of bounds - file may be wrong or torrent metadata mismatch. Discard this URL. |
| 503 Service Unavailable | Server overloaded - retry after delay (use Retry-After header if present) |
| 404 Not Found | File not available - discard this URL |
| Any 5xx | Server error - back off exponentially, retry |
| Hash mismatch | **Close connection immediately.** Discard the URL permanently. |

### BEP 17 Error Handling

| HTTP Status | Action |
|-------------|--------|
| 200 OK | Success - verify piece hash |
| 503 | Body contains ASCII integer: seconds to wait before retrying |
| Any other | Error - increase backoff, retry less frequently |
| Hash mismatch | Close connection, discard URL |

### Retry Strategy

A reasonable retry strategy for both protocols:

```
Initial retry delay:    30 seconds
After 3 failures:       60 seconds
After 6 failures:       120 seconds
After 10 failures:      300 seconds (5 minutes)
Max retry delay:        600 seconds (10 minutes)
Hash verification fail: Permanent ban (discard URL)
```

---

## Practical Implementation

### How Web Seeds Integrate with the Piece Manager

A web seed connection acts as a special kind of peer that:
1. **Always has all pieces** - it advertises a full bitfield
2. **Is always unchoked** - it never refuses requests (until HTTP errors occur)
3. **Uses HTTP instead of the wire protocol** - block requests become HTTP Range requests

The piece manager treats web seed connections like regular peers for piece selection, but the transport layer translates piece/block requests into HTTP operations.

### Priority: Web Seeds vs. Peer Connections

Web seeds typically have **lower priority** than peer connections for several reasons:

1. **Cost to the server operator:** HTTP bandwidth often costs money, while P2P bandwidth is "free" to the swarm
2. **Rate limiting:** Web seed servers may throttle connections, while peers can saturate available bandwidth
3. **Server load:** Too many clients hammering a web seed is effectively a DDoS

**Practical prioritization:**
- Use peers for pieces that are available from multiple sources
- Use web seeds primarily for pieces that no peers have (bootstrapping)
- Use web seeds as a fallback when peers are slow or the swarm is small
- Respect HTTP 503 / Retry-After headers - the server is telling you to back off

### Rate Limiting Considerations

**Client-side:**
- Don't open too many concurrent HTTP connections to the same server (2-4 max)
- Respect rate limit headers (`Retry-After`, `X-RateLimit-*`)
- Back off exponentially on errors
- Prefer downloading large contiguous ranges (fewer HTTP requests, less server log spam)

**Server-side (BEP 17):**
- Limit average upload rate per client IP
- Calculate and return appropriate retry delays in 503 responses
- Consider banning clients that ignore retry delays
- Monitor tracker to reduce/stop seeding when P2P swarm is healthy

### How WebTorrent Uses Web Seeds

WebTorrent (both the JavaScript reference implementation and SpawnDev.WebTorrent) uses the `url-list` field from BEP 19. Web seeds are particularly valuable in the WebTorrent ecosystem because:

1. **Bootstrap problem:** WebRTC-based peers can only connect through signaling servers. If no peers are in the swarm, HTTP web seeds provide the initial data.
2. **Browser compatibility:** HTTP requests work everywhere - no WebRTC setup needed for the seed.
3. **CDN integration:** Web seeds can be served from any CDN (CloudFlare, AWS S3, etc.), providing fast, reliable bootstrap data.

**url-list in torrent creation:**

When creating a torrent from a URL (e.g., `TorrentCreator.CreateFromUrlAsync`), the source URL is automatically included as a web seed in the `url-list` field. This means the original HTTP source always serves as a fallback seed.

**url-list in magnet URIs:**

The `ws=` parameter in magnet URIs carries web seed URLs:

```
magnet:?xt=urn:btih:<infohash>&ws=http://mirror.com/file.exe
```

Multiple web seeds use multiple `ws=` parameters:

```
magnet:?xt=urn:btih:<infohash>&ws=http://mirror1.com/file.exe&ws=http://mirror2.com/file.exe
```

### Connection Lifecycle

```
1. Parse url-list from torrent metadata (or ws= from magnet URI)
2. For each URL, create a WebConn (web seed connection)
3. WebConn creates a fake Wire that reports having all pieces
4. Piece manager requests blocks from the WebConn like any other peer
5. WebConn translates block requests into HTTP Range requests
6. Downloaded data is verified against piece hashes
7. On hash failure: destroy the WebConn and ban the URL
8. On HTTP error: retry with backoff, eventually destroy
9. On success: piece is shared with P2P peers normally
```

### Single-File vs. Multi-File Request Patterns

**Single-file:** Each block request becomes one HTTP Range request. Simple.

**Multi-file:** A block request may need data from multiple files. The WebConn:
1. Calculates which files the block spans (using the piece-to-file mapping algorithm)
2. Issues parallel HTTP Range requests to each file
3. Assembles the responses in order
4. Returns the assembled block to the piece manager

---

## SpawnDev.WebTorrent Implementation Notes

### Key Classes

| Class | File | Purpose |
|-------|------|---------|
| `WebConn` | WebConn.cs | Web seed connection - translates piece requests to HTTP Range requests |
| `Wire` | Wire.cs | Wire protocol - WebConn creates a fake Wire that reports all pieces |
| `TorrentCreator` | TorrentCreator.cs | Adds `url-list` when creating torrents from URLs |
| `TorrentParser` | TorrentParser.cs | Parses `url-list` and `httpseeds` from torrent metadata |
| `Torrent` | Torrent.cs | Manages WebConn instances alongside regular peer connections |

### WebConn Architecture

`WebConn` acts as a fake peer wire that:
1. Creates a `Wire` instance with type `"webSeed"`
2. On handshake, responds with a fake peer ID (SHA-1 hash of the URL)
3. Sends a bitfield with all bits set (has all pieces)
4. When the torrent sends a `request` message, translates it to HTTP Range requests
5. Returns the data as `piece` messages through the wire

**Constants (matching JS WebTorrent):**
- Socket timeout: 60,000 ms (60 seconds)
- Retry delay: 10,000 ms (10 seconds between retrying a piece after HTTP failure)

### BEP 17 vs BEP 19 Support

| BEP | Metadata Key | Supported | Notes |
|-----|-------------|-----------|-------|
| BEP 17 | `httpseeds` | Yes | HTTP range request web seeds |
| BEP 19 | `url-list` | Yes | Multi-file piece assembly, path-safe URL encoding |

Both protocols are supported. In practice, nearly all torrents use BEP 19 (`url-list`). BEP 17 (`httpseeds`) is rare but fully functional.

### URL Encoding for Multi-File Paths

When constructing URLs for multi-file torrents, file path components must be properly URL-encoded. Special characters in filenames (spaces, unicode, etc.) are percent-encoded to produce valid URLs:

```
Torrent path: ["My Album", "Track 01 (feat. Artist).mp3"]
URL:          http://mirror.com/pub/My%20Album/Track%2001%20%28feat.%20Artist%29.mp3
```

### Integration with HuggingFace Proxy

The `SpawnDev.WebTorrent.Server.HuggingFace` module automatically generates web seed URLs when creating torrent files for HuggingFace models:

```
GET /torrent/{org}/{repo}/{filePath}  - Returns .torrent with web seed pointing to:
GET /hf/{org}/{repo}/{filePath}       - Web seed endpoint (serves HTTP Range requests)
```

This means every HuggingFace model torrent automatically has a reliable web seed backed by the HuggingFace CDN, ensuring downloads can always start even with an empty P2P swarm.

### Platform Support

| Feature | Desktop | Browser |
|---------|---------|---------|
| BEP 17 (httpseeds) | Yes | Yes |
| BEP 19 (url-list) | Yes | Yes |
| Multi-file piece assembly | Yes | Yes |
| HTTP Range requests | System.Net.Http | Browser fetch API (via BlazorJS) |

Web seeds work identically on both platforms since they only require HTTP - no UDP or WebRTC needed.
