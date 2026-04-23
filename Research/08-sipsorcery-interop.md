# SipSorcery to Browser WebRTC Interop Analysis

## Problem Statement

SpawnDev.WebTorrent's desktop client uses SipSorcery 10.0.3 (NuGet) for WebRTC. It can connect to other SipSorcery peers (Desktop_TwoClients test passes in 7s) but CANNOT connect to browser-based JS WebTorrent peers (all 7 Sintel network tests fail).

The browser peers are running standard JS WebTorrent with `@thaunknown/simple-peer` (WebRTC abstraction layer) in Chrome/Firefox/Edge.

## Working Reference: SpawnDev.RTLink

SpawnDev.RTLink successfully connects SipSorcery peers to browser peers. Key difference: RTLink uses a **bundled, modified copy** of SipSorcery source code, NOT the NuGet package.

Location: `D:/users/tj/Projects/SpawnDev.RTLink/SpawnDev.RTLink/SIPSorcery/`

### RTLink's SipSorcery Setup

RTLink's working configuration:
- RTCConfiguration with only ICE servers (no special flags)
- No SDP munging/transforms at application layer
- SDP is passed as raw strings between peers
- Data channel named "RPC" (ordered, not negotiated)
- Trickle ICE with individual candidate exchange via relay

### Key Browser Compatibility Features in RTLink's SipSorcery

1. **DTLS fingerprint upper-cased**: Firefox requires upper-case hex in fingerprint
2. **Chrome ICE role fix**: Forces `IceRole = active` when receiving offers
3. **Modern SCTP format**: `m=application 9 UDP/DTLS/SCTP webrtc-datachannel`
4. **Accepts both legacy and modern**: `DTLS/SCTP` and `UDP/DTLS/SCTP`
5. **Data channel via DCEP**: In-band negotiation, not SDP-negotiated

## Our SipSorceryPeer Configuration

```csharp
// SipSorceryPeer.cs - our current setup
var config = new RTCConfiguration();
config.iceServers = new List<RTCIceServer>();
foreach (var url in _iceServers)
    config.iceServers.Add(new RTCIceServer { urls = url });

_pc = new RTCPeerConnection(config);
```

### Current Flow (Non-Trickle)

1. Create RTCPeerConnection
2. If initiator: createDataChannel, createOffer, setLocalDescription, waitForICE, emit SDP
3. If responder: ondatachannel handler, setRemoteDescription(offer), createAnswer, setLocalDescription, waitForICE, emit SDP
4. SDP is sent through WebSocket tracker as offer/answer

### SDP Transform

We apply `FilterTrickle()` which removes `a=ice-options:trickle\s\n`.

**Potential issue:** SipSorcery generates `a=ice-options:ice2,trickle` which does NOT match the regex `a=ice-options:trickle\s\n`. So the ice-options line remains. JS simple-peer also only removes exactly `a=ice-options:trickle\s\n`. So both sides may have slightly different ice-options content.

## Hypotheses for Interop Failure

### Hypothesis 1: SDP Format Differences (MOST LIKELY)

SipSorcery NuGet 10.0.3 may generate SDP that browsers reject or misparse.

Known SDP differences between SipSorcery and browsers:
- **Origin line**: SipSorcery uses `o=- <session_id> 0 IN IP4 <local_ip>`, browsers vary
- **ICE options**: SipSorcery adds `ice2,trickle`, browsers typically have just `trickle` or nothing
- **Fingerprint position**: Session-level vs media-level placement
- **Candidate format**: Different priority calculations
- **SCTP format**: `UDP/DTLS/SCTP` vs legacy `DTLS/SCTP`
- **Setup role**: `actpass` (offer) vs `active` (answer) - CRITICAL

### Hypothesis 2: Missing Patches in NuGet vs RTLink's Bundled Copy

RTLink's bundled SipSorcery may have critical browser compatibility patches that the official NuGet package lacks. This is the most actionable investigation.

### Hypothesis 3: ICE Gathering Timing

SipSorcery's ICE gathering may not include all candidates before we read the SDP. Our 2s candidate silence fallback + 15s timeout should handle this, but edge cases exist.

### Hypothesis 4: Data Channel Label Mismatch

JS simple-peer creates data channels with random hex labels (20 bytes). SipSorcery may have issues with certain label characters or lengths.

### Hypothesis 5: DTLS Handshake Failure

The DTLS handshake between SipSorcery and browser WebRTC may fail silently. SipSorcery uses a self-signed cert with specific parameters that may not be compatible with all browser DTLS implementations.

## ROOT CAUSE FOUND: Completely Rewritten DTLS Stack in SipSorcery 10.0.3

### The Smoking Gun

The RTLink vs NuGet 10.0.3 comparison revealed that **the entire DTLS/SRTP stack was rewritten** between the version RTLink bundles and the 10.0.3 NuGet package.

**RTLink (works with browsers) - Old BouncyCastle DTLS:**
- Uses `Org.BouncyCastle.Crypto.Tls` (old API)
- DtlsSrtpClient.cs: 453 lines, extends `DefaultTlsClient`
- DtlsSrtpServer.cs: 584 lines, extends `DefaultTlsServer`
- DtlsSrtpTransport.cs: 616 lines, full handshake management
- Only offers `SRTP_AES128_CM_HMAC_SHA1_80` (universally supported)

**NuGet 10.0.3 (fails with browsers) - New "SharpSRTP" by Lukas Volf (Dec 2025):**
- Uses `Org.BouncyCastle.Tls` (new API)
- DtlsSrtpClient.cs: 162 lines, extends new `DtlsClient`
- DtlsSrtpServer.cs: 131 lines, extends new `DtlsServer`
- DtlsSrtpTransport.cs: 177 lines, simplified with `SrtpSessionContext`
- Offers 12 SRTP profiles (AES-256-GCM, AES-128-GCM, ARIA, etc.)

### Why This Breaks Browser Interop

1. **SRTP profile negotiation**: Offering 12 profiles vs 1 could cause negotiation failures if the browser doesn't support the first offered profile, or if the profile ordering doesn't match browser preferences
2. **DTLS handshake implementation**: Entirely different code paths for the handshake. Any bug in the new SharpSRTP library's DTLS implementation with Chrome/Firefox would break connectivity
3. **Key derivation**: Different SRTP key extraction mechanism (old uses `IPacketTransformer`/`SrtpPolicy`, new uses `SrtpSessionContext`/`SrtpKeys`)
4. **Server has `ForceDisableMKI = true`**: RFC 8827 compliance change that may not be compatible with all browser implementations

### Other Differences Found

| Area | RTLink (works) | NuGet 10.0.3 (fails) |
|------|---------------|---------------------|
| `createAnswer()` IceRole | Doesn't explicitly enforce | Enforces RFC 4145 Section 4.1 |
| DTLS client construction | 2 params (cert, key) | 4 params (crypto, cert, key, sigAlg) |
| ICE gathering | Simple task + wait | Configurable timeout with CTS |
| Data channel cleanup | Not explicit | Explicit iteration + close |
| DTLS fingerprint computation | Local BouncyCastle digest | Delegates to SharpSRTP library |

### Items That Are IDENTICAL (Ruled Out)

- SDP format: both produce `m=application 9 UDP/DTLS/SCTP webrtc-datachannel`
- DTLS fingerprint toString: both use upper-case hex
- ICE role handling in setRemoteDescription: identical logic
- ICE candidate format: identical
- SDPMediaAnnouncement serialization: identical
- Default IceRole: both `actpass`
- ICE options: both `ice2,trickle`

## Fix Options

### Option A: Bundle RTLink's SipSorcery (Fastest)
Copy RTLink's bundled SipSorcery source into SpawnDev.WebTorrent and build against it instead of the NuGet package. This is proven to work with browsers.

**Pros:** Known working, fast to implement
**Cons:** Maintaining a forked copy, missing newer SipSorcery features

### Option B: Pin to Older SipSorcery Version
Find the last SipSorcery NuGet version that uses the old DTLS stack (before SharpSRTP rewrite). Pin to that version.

**Pros:** Uses official package, no source modification
**Cons:** Need to identify the correct version, may miss other fixes

### Option C: Fix the New DTLS Stack
Add detailed DTLS handshake logging, identify exactly where the new stack fails with browsers, and fix it.

**Pros:** Fixes the root cause, benefits all SipSorcery users
**Cons:** Most time-consuming, requires deep DTLS knowledge

### Option D: Restrict SRTP Profiles
Configure the new SipSorcery to only offer `SRTP_AES128_CM_HMAC_SHA1_80` (the profile RTLink's version offers). This may be the simplest fix if the issue is profile negotiation.

**Pros:** Minimal change, tests a specific hypothesis
**Cons:** May not be the actual issue if the DTLS handshake itself is broken

## Investigation Plan

1. **Try Option D first** - restrict SRTP profiles (lowest effort, tests hypothesis)
2. **Add DTLS handshake logging** - log every state transition to find where it fails
3. **If DTLS is broken: try Option B** - find the last working NuGet version
4. **If no older version works: try Option A** - bundle RTLink's copy
5. **Long term: fix upstream** - report issue to SipSorcery repo with reproduction

## Files to Investigate

### Our Implementation
- `SpawnDev.WebTorrent/SipSorceryPeer.cs` - Our WebRTC peer
- `SpawnDev.WebTorrent/SimplePeer.cs` - Base class
- `SpawnDev.WebTorrent/WebSocketTracker.cs` - Tracker signaling

### RTLink Reference
- `SpawnDev.RTLink/RTLinkSIPSorc/RPCWebRTCSIPConnection.cs` - Working SipSorcery peer
- `SpawnDev.RTLink/SIPSorcery/net/WebRTC/RTCPeerConnection.cs` - Modified SipSorcery source
- `SpawnDev.RTLink/SIPSorcery/net/SDP/SDPMediaAnnouncement.cs` - SDP generation

### JS Reference
- `webtorrent-reference-client/node_modules/@thaunknown/simple-peer/lite.js` - JS WebRTC

## Test Results

| Test | Status | Notes |
|------|--------|-------|
| Desktop_TwoClients | PASS (7s) | SipSorcery-to-SipSorcery works |
| CSharp_Downloads_From_JsWebTorrent | PASS (13s) | JS-to-C# via our tracker |
| Network_TrackerConnect_Announces | FAIL | SipSorcery can't reach Sintel browser peers |
| Network_MagnetAdd_PeersFound | FAIL | Same issue |
| Network_MagnetAdd_MetadataReceived | FAIL | Same issue |
| Network_LiveSwarm_DownloadsPieces | FAIL | Same issue |
| + 3 more Sintel tests | FAIL | All same root cause |
