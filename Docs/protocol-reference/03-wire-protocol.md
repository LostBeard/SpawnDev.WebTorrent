# BitTorrent Wire Protocol (BEP 3 + BEP 6 + BEP 10)

## Connection

After the WebRTC data channel opens, peers exchange the BT wire protocol over the data channel.
All data is binary, big-endian.

## Handshake (68 bytes, sent FIRST by both peers)

| Offset | Size | Field | Value |
|--------|------|-------|-------|
| 0 | 1 | pstrlen | `19` (0x13) |
| 1 | 19 | pstr | `"BitTorrent protocol"` (ASCII) |
| 20 | 8 | reserved | 8 bytes, extension flags (see below) |
| 28 | 20 | info_hash | SHA-1 hash of the torrent's info dict |
| 48 | 20 | peer_id | Sender's 20-byte peer ID |

### Reserved Bytes (Extension Flags)

| Byte | Bit | Extension |
|------|-----|-----------|
| 5 | 4 (0x10) | BEP 10 Extension Protocol |
| 7 | 0 (0x01) | DHT (BEP 5) |
| 7 | 2 (0x04) | Fast Extension (BEP 6) |

Example: `0x0000000000100005` = Extended + DHT + Fast

## Messages (after handshake)

| Length | ID | Name | Payload |
|--------|----|------|---------|
| 0 | - | keep-alive | (none) |
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
| 1 | 14 | have_all | (none) - BEP 6 |
| 1 | 15 | have_none | (none) - BEP 6 |
| 2+N | 20 | extended | uint8 ext_id, N bytes payload - BEP 10 |

### Bitfield Encoding

Each bit represents a piece. Bit 7 of byte 0 = piece 0, bit 6 = piece 1, etc.
Spare bits at the end are zero.

Example for 3 pieces: `0xE0` = `11100000` = pieces 0, 1, 2 all present.

## Typical Message Sequence

1. Both peers send handshake simultaneously
2. Seeder sends bitfield (or have_all if BEP 6)
3. Downloader sends interested
4. Seeder sends unchoke
5. Downloader sends request(s)
6. Seeder sends piece(s)
7. Repeat until done
8. Downloader sends not_interested

## Captured Wire Events

### [+3585ms] connected (downloader)

```json
{
  "event": "connected",
  "role": "downloader",
  "remote_peer_id": "2d5757303230382d32774575423479702b536358",
  "remote_peer_id_hex": "32643537353733303332333033383264333237373435373534323334373937303262353336333538"
}
```

### [+3586ms] connected (seeder)

```json
{
  "event": "connected",
  "role": "seeder",
  "remote_peer_id": "2d5757303230382d5369617a37466a486b723244",
  "remote_peer_id_hex": "32643537353733303332333033383264353336393631376133373436366134383662373233323434"
}
```

### [+3586ms] have_all (downloader)

```json
{
  "event": "have_all",
  "role": "downloader",
  "direction": "received"
}
```

### [+3587ms] port (downloader)

```json
{
  "event": "port",
  "role": "downloader",
  "direction": "received",
  "port": 56049
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

### [+3591ms] port (seeder)

```json
{
  "event": "port",
  "role": "seeder",
  "direction": "received",
  "port": 56698
}
```

