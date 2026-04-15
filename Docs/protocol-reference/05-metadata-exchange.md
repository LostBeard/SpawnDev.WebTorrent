# Metadata Exchange (BEP 9 - ut_metadata)

## Overview

When a peer joins via magnet link, it doesn't have the torrent's info dict.
ut_metadata allows downloading the info dict from peers who have it.

## Protocol

### Step 1: Extended Handshake

The seeder includes `metadata_size` in its extended handshake.
Both peers include `ut_metadata` in their `m` dict.

### Step 2: Request

The downloader sends a ut_metadata request:

```
Bencoded: d8:msg_typei0e5:piecei0ee
```

| Key | Value | Description |
|-----|-------|-------------|
| `msg_type` | 0 | Request |
| `piece` | 0..N | Metadata piece index (16KB per piece) |

### Step 3: Response

The seeder responds with the metadata:

```
Bencoded: d8:msg_typei1e5:piecei0e10:total_sizei139ee + [raw info dict bytes]
```

| Key | Value | Description |
|-----|-------|-------------|
| `msg_type` | 1 | Data |
| `piece` | 0..N | Metadata piece index |
| `total_size` | int | Total metadata size in bytes |

The raw info dict bytes follow immediately after the bencoded dict.

### Step 4: Verification

The downloader concatenates all pieces and verifies the SHA-1 hash
matches the info_hash from the magnet link.

## Captured Metadata Events

### [+3586ms] handshake (downloader)

```json
{
  "event": "extended",
  "role": "downloader",
  "direction": "received",
  "extension_name": "handshake",
  "payload_length": 0,
  "peer_handshake": {
    "m": {
      "lt_donthave": 3,
      "ut_metadata": 1,
      "ut_pex": 2
    },
    "metadata_size": 139
  },
  "payload_decoded": {
    "m": {
      "lt_donthave": 3,
      "ut_metadata": 1,
      "ut_pex": 2
    },
    "metadata_size": 139
  }
}
```

### [+3587ms] handshake (seeder)

```json
{
  "event": "extended",
  "role": "seeder",
  "direction": "received",
  "extension_name": "handshake",
  "payload_length": 0,
  "peer_handshake": {
    "m": {
      "lt_donthave": 3,
      "ut_metadata": 1,
      "ut_pex": 2
    }
  },
  "payload_decoded": {
    "m": {
      "lt_donthave": 3,
      "ut_metadata": 1,
      "ut_pex": 2
    }
  }
}
```

### [+3588ms] ut_metadata (seeder)

```json
{
  "event": "extended",
  "role": "seeder",
  "direction": "received",
  "extension_name": "ut_metadata",
  "payload_length": 25,
  "payload_hex_first_128": "64383a6d73675f74797065693065353a706965636569306565"
}
```

### [+3588ms] ut_metadata (downloader)

```json
{
  "event": "extended",
  "role": "downloader",
  "direction": "received",
  "extension_name": "ut_metadata",
  "payload_length": 182,
  "payload_decoded": {
    "0": 100,
    "1": 56,
    "2": 58,
    "3": 109,
    "4": 115,
    "5": 103,
    "6": 95,
    "7": 116,
    "8": 121,
    "9": 112,
    "10": 101,
    "11": 105,
    "12": 49,
    "13": 101,
    "14": 53,
    "15": 58,
    "16": 112,
    "17": 105,
    "18": 101,
    "19": 99,
    "20": 101,
    "21": 105,
    "22": 48,
    "23": 101,
    "24": 49,
    "25": 48,
    "26": 58,
    "27": 116,
    "28": 111,
    "29": 116,
    "30": 97,
    "31": 108,
    "32": 95,
    "33": 115,
    "34": 105,
    "35": 122,
    "36": 101,
    "37": 105,
    "38": 49,
    "39": 51,
    "40": 57,
    "41": 101,
    "42": 101,
    "43": 100,
    "44": 54,
    "45": 58,
    "46": 108,
    "47": 101,
    "48": 110,
    "49": 103,
    "50": 116,
    "51": 104,
    "52": 105,
    "53": 52,
    "54": 57,
    "55": 49,
    "56": 53,
    "57": 50,
    "58": 101,
    "59": 52,
    "60": 58,
    "61": 110,
    "62": 97,
    "63": 109,
    "64": 101,
    "65": 50,
    "66": 48,
    "67": 58,
    "68": 112,
    "69": 114,
    "70": 111,
    "71": 116,
    "72": 111,
    "73": 99,
    "74": 111,
    "75": 108,
    "76": 45,
    "77": 99,
    "78": 97,
    "79": 112,
    "80": 116,
    "81": 117,
    "82": 114,
    "83": 101,
    "84": 46,
    "85": 98,
    "86": 105,
    "87": 110,
    "88": 49,
    "89": 50,
    "90": 58,
    "91": 112,
    "92": 105,
    "93": 101,
    "94": 99,
    "95": 101,
    "96": 32,
    "97": 108,
    "98": 101,
    "99": 110,
    "100": 103,
    "101": 116,
    "102": 104,
    "103": 105,
    "104": 49,
    "105": 54,
    "106": 51,
    "107": 56,
    "108": 52,
    "109": 101,
    "110": 54,
    "111": 58,
    "112": 112,
    "113": 105,
    "114": 101,
    "115": 99,
    "116": 101,
    "117": 115,
    "118": 54,
    "119": 48,
    "120": 58,
    "121": 195,
    "122": 25,
    "123": 69,
    "124": 251,
    "125": 95,
    "126": 218,
    "127": 99,
    "128": 133,
    "129": 196,
    "130": 171,
    "131": 154,
    "132": 95,
    "133": 188,
    "134": 148,
    "135": 211,
    "136": 189,
    "137": 124,
    "138": 144,
    "139": 10,
    "140": 224,
    "141": 195,
    "142": 25,
    "143": 69,
    "144": 251,
    "145": 95,
    "146": 218,
    "147": 99,
    "148": 133,
    "149": 196,
    "150": 171,
    "151": 154,
    "152": 95,
    "153": 188,
    "154": 148,
    "155": 211,
    "156": 189,
    "157": 124,
    "158": 144,
    "159": 10,
    "160": 224,
    "161": 195,
    "162": 25,
    "163": 69,
    "164": 251,
    "165": 95,
    "166": 218,
    "167": 99,
    "168": 133,
    "169": 196,
    "170": 171,
    "171": 154,
    "172": 95,
    "173": 188,
    "174": 148,
    "175": 211,
    "176": 189,
    "177": 124,
    "178": 144,
    "179": 10,
    "180": 224,
    "181": 101
  }
}
```

### [+3589ms] downloader_metadata_received

```json
{
  "event": "downloader_metadata_received",
  "name": "protocol-capture.bin",
  "piece_count": 3,
  "piece_length": 16384,
  "total_length": 49152,
  "info_hash": "863e15ae3ac365c56bfbd1139401ece3a55f8422"
}
```

