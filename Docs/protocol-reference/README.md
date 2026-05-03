# WebTorrent Protocol Reference

Complete protocol documentation captured from a live WebTorrent session using
instrumented JS WebTorrent client and bittorrent-tracker server.

Generated: 2026-04-15T03:15:49.732Z

## Session Parameters

| Parameter | Value |
|---|---|
| Tracker | `ws://127.0.0.1:18900` (local bittorrent-tracker) |
| Seeder Peer ID | `2d5757303230382d32774575423479702b536358` |
| Downloader Peer ID | `2d5757303230382d5369617a37466a486b723244` |
| Info Hash | `863e15ae3ac365c56bfbd1139401ece3a55f8422` |
| File | `protocol-capture.bin` (49152 bytes) |
| Pieces | 3 x 16384 bytes |
| Total Events Captured | 27 |

## Documents

1. [Tracker WebSocket Protocol](01-tracker-websocket.md) - Announce, offer/answer relay, binary encoding
2. [WebRTC Signaling](02-webrtc-signaling.md) - Full SDP offers and answers
3. [Wire Protocol](03-wire-protocol.md) - BT handshake, bitfield, choke/unchoke, have
4. [BEP 10 Extended Protocol](04-bep10-extended.md) - Extended handshake, extension negotiation
5. [Metadata Exchange](05-metadata-exchange.md) - ut_metadata (BEP 9)
6. [Piece Exchange](06-piece-exchange.md) - Request, piece data, download completion
7. [Full Session Transcript](07-full-transcript.md) - Every event in chronological order
8. [Offer Pairing and Duplicate-PC Prevention](08-offer-pairing-and-dedup.md) - Tracker offer pool, positional pairing, client-side dedup invariants, our gaps as of 2026-05-03
