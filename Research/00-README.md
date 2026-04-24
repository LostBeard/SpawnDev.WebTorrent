# WebTorrent Protocol Research & Documentation

Comprehensive protocol documentation for the WebTorrent ecosystem. This documentation serves two purposes:

1. **Internal reference** for the SpawnDev.WebTorrent C# implementation
2. **Community documentation** - clear, complete protocol docs that the WebTorrent community currently lacks

## Documents

| # | Document | Description | Status |
|---|----------|-------------|--------|
| 00 | [README.md](00-README.md) | This index | Complete |
| 01 | [01-wire-protocol.md](01-wire-protocol.md) | BitTorrent wire protocol (BEP 3, 6, 10) | Complete |
| 02 | [02-webtorrent-protocol.md](02-webtorrent-protocol.md) | WebTorrent-specific: tracker, WebRTC, data channels | Complete |
| 03 | [03-extension-protocols.md](03-extension-protocols.md) | BEP 9 (ut_metadata), BEP 11 (ut_pex), lt_donthave | Complete |
| 04 | [04-tracker-protocols.md](04-tracker-protocols.md) | HTTP, UDP, WebSocket tracker protocols | Complete |
| 05 | [05-dht-protocol.md](05-dht-protocol.md) | DHT (BEP 5), mutable items (BEP 44/46) | Complete |
| 06 | [06-web-seeds.md](06-web-seeds.md) | BEP 17, BEP 19 web seed protocols | Complete |
| 07 | [07-lifecycle.md](07-lifecycle.md) | Master lifecycle - full order of operations | Complete |
| 08 | [08-sipsorcery-interop.md](08-sipsorcery-interop.md) | SipSorcery/browser WebRTC interop analysis | Complete |
| 09 | [09-sipsorcery-dtls-analysis.md](09-sipsorcery-dtls-analysis.md) | SipSorcery DTLS/SRTP fork rationale (why we fork vs upstream) | Complete |

## Existing Protocol Reference

Captured protocol data from instrumented JS WebTorrent sessions lives at:
- `../Docs/protocol-reference/` - 7 documents with raw capture data from 2026-04-14

## Sources

- BEP specifications: bittorrent.org/beps/
- JS WebTorrent source: `webtorrent` npm package
- JS simple-peer source: `@thaunknown/simple-peer` npm package
- JS bittorrent-tracker source: `bittorrent-tracker` npm package
- JS bittorrent-protocol source: `bittorrent-protocol` npm package
- SpawnDev.RTLink SipSorcery patterns: proven WebRTC browser interop
- Captured protocol data from live sessions

## Contributing

This documentation is maintained alongside the SpawnDev.WebTorrent project. If you find errors or want to add detail, contributions are welcome.

## License

This documentation is part of the SpawnDev.WebTorrent project and is provided under the same license.
