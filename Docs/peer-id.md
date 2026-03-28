# Peer ID Convention

SpawnDev.WebTorrent uses the [Azureus-style peer ID](http://bittorrent.org/beps/bep_0020.html) format:

```
-XXYYYY-RRRRRRRRRRRR
```

- **XX** — 2-character client identifier
- **YYYY** — 4-digit version number
- **RRRRRRRRRRRR** — 12 random bytes (unique per session)

## Our Peer ID

| Field | Value | Meaning |
|-------|-------|---------|
| Client ID | `SD` | **S**pawn**D**ev |
| Version | `0110` | v1.1.0 |
| Full prefix | `-SD0110-` | SpawnDev.WebTorrent v1.1.0 |

## Version History

| Version | Peer ID | Release |
|---------|---------|---------|
| 1.0.0 | `-SD0100-` | 2026-03-27 — Initial NuGet release |
| 1.1.0 | `-SD0110-` | 2026-03-28 — Real WebRTC P2P, 12 BEPs, 150 tests |

## Version Encoding

The 4-digit version maps to semver as: `MMNN` where MM = major*10 + minor, NN = patch.

| Semver | Peer ID digits |
|--------|---------------|
| 1.0.0 | `0100` |
| 1.1.0 | `0110` |
| 1.2.0 | `0120` |
| 2.0.0 | `0200` |

## How Other Clients Identify Us

Any BitTorrent client or tracker that inspects peer IDs will see `-SD####-` and can identify us as SpawnDev.WebTorrent. Common client IDs for reference:

| Client | ID |
|--------|-----|
| SpawnDev.WebTorrent | `SD` |
| qBittorrent | `qB` |
| Transmission | `TR` |
| WebTorrent (JS) | `WW` |
| Deluge | `DE` |
| libtorrent | `LT` |

## Implementation

The peer ID is generated in `WebTorrentClient.cs`:

```csharp
_peerId = new byte[20];
"-SD0110-"u8.CopyTo(_peerId);
Random.Shared.NextBytes(_peerId.AsSpan(8));
```

When bumping versions, update:
1. `WebTorrentClient.cs` — the peer ID prefix string
2. `WebTorrentTestBase.cs` — the `Client_PeerId_Format` test assertion
3. This document
