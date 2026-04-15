# Piece Exchange

## Overview

After metadata is received and verified, the downloader requests pieces from peers.

## Flow

1. Seeder sends `bitfield` (or `have_all`) showing which pieces it has
2. Downloader sends `interested` (message ID 2)
3. Seeder sends `unchoke` (message ID 1)
4. Downloader sends `request` messages (message ID 6)
5. Seeder responds with `piece` messages (message ID 7)
6. After all pieces: downloader sends `not_interested` (message ID 3)

## Request Format

```
[length=13][id=6][index:uint32][begin:uint32][length:uint32]
```

- `index`: piece index (0-based)
- `begin`: byte offset within the piece
- `length`: number of bytes requested (typically 16384 = 16KB)

## Piece Format

```
[length=9+N][id=7][index:uint32][begin:uint32][data:N bytes]
```

## Piece Verification

After receiving all blocks for a piece, the client computes SHA-1 (or SHA-256)
of the assembled piece and compares against the hash in the info dict.

## Captured Piece Exchange Events

### [+3586ms] have_all (downloader)

```json
{
  "event": "have_all",
  "role": "downloader",
  "direction": "received"
}
```

### [+3591ms] have_all (seeder)

```json
{
  "event": "have_all",
  "role": "seeder",
  "direction": "received"
}
```

### [+3590ms] downloader_done

```json
{
  "event": "downloader_done",
  "downloaded": 49152,
  "uploaded": 157,
  "peers": 1,
  "ratio": 0.5627240143369175
}
```

