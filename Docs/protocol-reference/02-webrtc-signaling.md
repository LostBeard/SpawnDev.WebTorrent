# WebRTC Signaling

## Overview

WebRTC connections are established via SDP offer/answer exchange relayed through the tracker.
WebTorrent uses non-trickle ICE - all ICE candidates are gathered before sending the SDP.

## SDP Structure

The SDP for WebTorrent data channels contains:
- `o=` line: origin (implementation-specific, e.g., `o=rtc ...` for node, `o=- ...` for browser)
- `a=group:BUNDLE`: media bundling
- `a=fingerprint:sha-256`: DTLS certificate fingerprint
- `m=application ... UDP/DTLS/SCTP webrtc-datachannel`: data channel media line
- `a=sctp-port:5000`: SCTP port for data channels
- `a=setup:actpass` (offer) or `a=setup:active` (answer)
- `a=ice-ufrag` / `a=ice-pwd`: ICE credentials
- `a=candidate`: ICE candidates (host, srflx, relay)
- `a=end-of-candidates`: marks end of candidate list

## Data Channel

WebTorrent creates an ordered, reliable data channel.
simple-peer uses label `"dc"` by default.
Max message size: 262144 bytes (256KB).

## Captured SDPs

### Offer from `2d5757303230382d32774575423479702b536358`

**Offer ID:** `26bf8f3585e2be678f0da2a3fd4a65e1cc264ccf`

```
v=0
o=rtc 3374675295 0 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic:WMS *
a=ice-options:ice2,trickle
a=fingerprint:sha-256 57:90:89:53:7A:DE:03:06:0C:C0:2D:21:C7:2A:03:73:CD:37:F0:6C:2C:8C:81:7E:53:FF:FB:FB:60:38:04:2B
m=application 55204 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 192.168.1.120
a=mid:0
a=sendrecv
a=sctp-port:5000
a=max-message-size:262144
a=setup:actpass
a=ice-ufrag:TC8V
a=ice-pwd:JpeiYW9htg3PQxpDvxZSc0
a=candidate:1 1 UDP 2114977791 192.168.1.120 55204 typ host
a=candidate:2 1 UDP 1678769919 64.246.234.108 55204 typ srflx raddr 0.0.0.0 rport 0
a=end-of-candidates

```

### Offer from `2d5757303230382d32774575423479702b536358`

**Offer ID:** `ff5233428a90a9720d68b5fe8d54b3c3c8a9b757`

```
v=0
o=rtc 2610308160 0 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic:WMS *
a=ice-options:ice2,trickle
a=fingerprint:sha-256 A9:D2:39:F2:A2:3E:C1:84:E6:6F:E0:BB:C5:BF:D2:3B:91:56:B8:29:F8:8F:2A:E6:1A:BC:6D:E8:5E:9D:EF:49
m=application 55205 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 192.168.1.120
a=mid:0
a=sendrecv
a=sctp-port:5000
a=max-message-size:262144
a=setup:actpass
a=ice-ufrag:s0x6
a=ice-pwd:QdW5m9EkZ1KTixl9oOPG7Z
a=candidate:1 1 UDP 2114977791 192.168.1.120 55205 typ host
a=candidate:2 1 UDP 1678769919 64.246.234.108 55205 typ srflx raddr 0.0.0.0 rport 0
a=end-of-candidates

```

### Offer from `2d5757303230382d32774575423479702b536358`

**Offer ID:** `5d5482331b7b2551268ca7bb37298eda4b23c03d`

```
v=0
o=rtc 1303727296 0 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic:WMS *
a=ice-options:ice2,trickle
a=fingerprint:sha-256 F5:9C:7A:7C:B8:EF:25:AA:D7:F3:80:37:D7:3A:EC:99:F0:ED:D4:36:B9:40:74:3B:6B:43:75:D6:5D:DF:05:BF
m=application 55206 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 192.168.1.120
a=mid:0
a=sendrecv
a=sctp-port:5000
a=max-message-size:262144
a=setup:actpass
a=ice-ufrag:H2yS
a=ice-pwd:Wk9o8cvPBw/VrdgtFTIoBq
a=candidate:1 1 UDP 2114977791 192.168.1.120 55206 typ host
a=candidate:2 1 UDP 1678769919 64.246.234.108 55206 typ srflx raddr 0.0.0.0 rport 0
a=end-of-candidates

```

### Offer from `2d5757303230382d32774575423479702b536358`

**Offer ID:** `35e57462e118ee04583e7a97993216f94aba87b8`

```
v=0
o=rtc 4099561181 0 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic:WMS *
a=ice-options:ice2,trickle
a=fingerprint:sha-256 AC:03:99:18:27:5C:D0:AB:A8:8C:F9:16:EA:B9:02:A9:7B:DB:F9:F8:5C:F5:B5:7D:47:58:1E:AC:FC:53:00:78
m=application 55207 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 192.168.1.120
a=mid:0
a=sendrecv
a=sctp-port:5000
a=max-message-size:262144
a=setup:actpass
a=ice-ufrag:CJiU
a=ice-pwd:gAVN1xRfLGAIVNiA4Nlu/2
a=candidate:1 1 UDP 2114977791 192.168.1.120 55207 typ host
a=candidate:2 1 UDP 1678769919 64.246.234.108 55207 typ srflx raddr 0.0.0.0 rport 0
a=end-of-candidates

```

### Offer from `2d5757303230382d32774575423479702b536358`

**Offer ID:** `a291562f866cd2a564ac178692a27d7fc2f46c89`

```
v=0
o=rtc 567055318 0 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic:WMS *
a=ice-options:ice2,trickle
a=fingerprint:sha-256 28:32:3B:CB:EC:9C:22:F6:0C:1D:1B:D2:C2:8B:8B:EA:AD:0E:51:8F:F1:65:F0:D7:EB:FA:1D:73:EB:09:3F:BB
m=application 55208 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 192.168.1.120
a=mid:0
a=sendrecv
a=sctp-port:5000
a=max-message-size:262144
a=setup:actpass
a=ice-ufrag:Y9d4
a=ice-pwd:9OIfMt5eqSLrc5LIPDEbkn
a=candidate:1 1 UDP 2114977791 192.168.1.120 55208 typ host
a=candidate:2 1 UDP 1678769919 64.246.234.108 55208 typ srflx raddr 0.0.0.0 rport 0
a=end-of-candidates

```

### Relayed Offer (tracker->client)

**From Peer:** `2d5757303230382d32774575423479702b536358`

```
v=0
o=rtc 3374675295 0 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic:WMS *
a=ice-options:ice2,trickle
a=fingerprint:sha-256 57:90:89:53:7A:DE:03:06:0C:C0:2D:21:C7:2A:03:73:CD:37:F0:6C:2C:8C:81:7E:53:FF:FB:FB:60:38:04:2B
m=application 55204 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 192.168.1.120
a=mid:0
a=sendrecv
a=sctp-port:5000
a=max-message-size:262144
a=setup:actpass
a=ice-ufrag:TC8V
a=ice-pwd:JpeiYW9htg3PQxpDvxZSc0
a=candidate:1 1 UDP 2114977791 192.168.1.120 55204 typ host
a=candidate:2 1 UDP 1678769919 64.246.234.108 55204 typ srflx raddr 0.0.0.0 rport 0
a=end-of-candidates

```

