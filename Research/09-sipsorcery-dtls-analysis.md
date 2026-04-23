# SIPSorcery DTLS/SRTP Stack Analysis - Old vs New

Research for SpawnDev.RTLink SIPSorcery fork planning.

---

## Executive Summary

The RTLink project bundles a modified copy of SIPSorcery v6.0.11 (the "OLD stack") that uses
Portable.BouncyCastle 1.9.0 and the original Rafael Soares DTLS/SRTP implementation.
This stack successfully connects to browser WebRTC peers.

SIPSorcery 10.0.3+ (the "NEW stack") contains a complete DTLS/SRTP rewrite called "SharpSRTP"
by Lukas Volf (GitHub: jimm98y), merged Dec 30 2025 via PR #1486. It uses
BouncyCastle.Cryptography 2.6.2 and the new `Org.BouncyCastle.Tls` namespace. This rewrite
has multiple known bugs and interoperability failures with browser peers.

**Recommendation:** Fork SIPSorcery, keep the OLD stack architecture with its proven BouncyCastle
`Org.BouncyCastle.Crypto.Tls` API surface, selectively upgrade only what is needed (ECDSA certs,
modern cipher suites), and avoid the SharpSRTP rewrite entirely.

---

## 1. Version Identification

### OLD Stack (RTLink bundled copy)
- **SIPSorcery version:** 6.0.11
- **BouncyCastle:** Portable.BouncyCastle 1.9.0
- **Namespace:** `Org.BouncyCastle.Crypto.Tls`
- **Author:** Rafael Soares (raf.csoares@kyubinteractive.com), July 2020
- **Origin:** Ported from RestComm/media-core Java implementation
- **License:** BSD 3-Clause (with AGPL-3.0 origin for some derived files)

### NEW Stack (sipsorcery-master / NuGet 10.0.3+)
- **SIPSorcery version:** 10.0.4-pre (source at D:\users\tj\Projects\sipsorcery-master)
- **BouncyCastle:** BouncyCastle.Cryptography 2.6.2
- **Namespace:** `Org.BouncyCastle.Tls` (completely different API)
- **SharpSRTP Author:** Lukas Volf (jimm98y), December 2025
- **License:** MIT (SharpSRTP), BSD 3-Clause (SIPSorcery wrapper)
- **PR:** #1486 "New DTLS-SRTP implementation based upon SharpSRTP" merged 2026-01-04
- **Follow-up PRs:**
  - #1495 "DTLS-SRTP code cleanup and fix for infinite DtlsServer handshake timeout" (merged 2026-01-19)
  - #1499 "SRTP unprotect failed after ~4-5 min: receiver ROC never updates" (merged 2026-01-29)
  - #1555 "SRTP Performance improvements and bugfixes" (merged 2026-04-14)
- **Source repo is NOT a git clone** - downloaded zip, no git history available

---

## 2. File Inventory

### OLD Stack Files (18 files)
Location: `D:\users\tj\Projects\SpawnDev.RTLink\SpawnDev.RTLink\SIPSorcery\net\DtlsSrtp\`

**Core DTLS:**
- `DtlsSrtpClient.cs` (453 lines) - DTLS client, extends `DefaultTlsClient`
- `DtlsSrtpServer.cs` (584 lines) - DTLS server, extends `DefaultTlsServer`
- `DtlsSrtpTransport.cs` (616 lines) - Transport layer, implements `DatagramTransport`
- `DtlsUtils.cs` (683 lines) - Certificate generation, fingerprints, credential loading

**SRTP:**
- `SrtpParameters.cs` (105 lines) - Protection profile configs
- `SrtpPolicy.cs` (96 lines) - Encryption/auth policy
- `SrtpHandler.cs` (249 lines) - SDES-SRTP handler
- `SecureContext.cs` (38 lines) - RTP session secure context

**Transform (SRTP crypto engine):**
- `Transform/IPackerTransformer.cs` - Packet transformer interface
- `Transform/ITransformEngine.cs` - Transform engine interface
- `Transform/RawPacket.cs` - Raw packet representation
- `Transform/SrtpCipherCTR.cs` - AES-CTR cipher
- `Transform/SrtpCipherF8.cs` - AES-F8 cipher
- `Transform/SrtpCryptoContext.cs` - RTP crypto context
- `Transform/SrtcpCryptoContext.cs` - RTCP crypto context
- `Transform/SrtpTransformEngine.cs` - Engine factory
- `Transform/SrtpTransformer.cs` - RTP transformer
- `Transform/SrtcpTransformer.cs` - RTCP transformer

### NEW Stack Files (26 files)
Location: `D:\users\tj\Projects\sipsorcery-master\src\SIPSorcery\net\DtlsSrtp\`

**Wrapper layer (kept from old):**
- `DtlsSrtpTransport.cs` (177 lines) - Heavily rewritten, delegates to SharpSRTP
- `DtlsUtils.cs` (79 lines) - Stripped to thin wrapper around SharpSRTP
- `SecureContext.cs` (35 lines) - Unchanged
- `SrtpHandler.cs` (155 lines) - Rewritten to use SharpSRTP

**SharpSRTP Lib/DTLS:**
- `Lib/Log.cs` - Custom logging bridge
- `Lib/DTLS/DtlsClient.cs` (428 lines) - New client, extends `DefaultTlsClient`
- `Lib/DTLS/DtlsServer.cs` (425 lines) - New server, extends `DefaultTlsServer`
- `Lib/DTLS/IDtlsPeer.cs` (128 lines) - Interface + enums + event args
- `Lib/DTLS/DtlsCertificateUtils.cs` (215 lines) - Certificate generation

**SharpSRTP Lib/DTLSSRTP:**
- `Lib/DTLSSRTP/DtlsSrtpClient.cs` (162 lines) - SRTP-aware client
- `Lib/DTLSSRTP/DtlsSrtpServer.cs` (131 lines) - SRTP-aware server
- `Lib/DTLSSRTP/IDtlsSrtpPeer.cs` (48 lines) - Interface + events
- `Lib/DTLSSRTP/DtlsSrtpKeys.cs` (51 lines) - Key container
- `Lib/DTLSSRTP/DtlsSrtpProtocol.cs` (201 lines) - Key derivation + session creation

**SharpSRTP Lib/SRTP:**
- `Lib/SRTP/ISrtpContext.cs` (33 lines)
- `Lib/SRTP/SrtpContext.cs` (600+ lines) - Main SRTP protect/unprotect
- `Lib/SRTP/SrtpKeys.cs` (46 lines)
- `Lib/SRTP/SrtpProtectionProfile.cs` (83 lines)
- `Lib/SRTP/SrtpProtocol.cs` (144 lines)
- `Lib/SRTP/SrtpSessionContext.cs` (69 lines)
- `Lib/SRTP/Authentication/HMAC.cs`
- `Lib/SRTP/Encryption/AEAD.cs` (68 lines) - GCM AEAD encryption
- `Lib/SRTP/Encryption/CTR.cs` (133 lines) - AES-CTR encryption
- `Lib/SRTP/Encryption/F8.cs`
- `Lib/SRTP/Readers/RtcpReader.cs`
- `Lib/SRTP/Readers/RtpReader.cs`

---

## 3. DTLS Handshake Flow Comparison

### OLD Stack Handshake

1. `DtlsSrtpTransport.DoHandshake()` determines client/server role
2. Creates `DtlsClientProtocol` or `DtlsServerProtocol` (BouncyCastle)
3. **Client path:** `clientProtocol.Connect(client, this)` - blocking call
4. **Server path:** `serverProtocol.Accept(server, this)` - blocking call
5. Transport implements `DatagramTransport` interface for packet I/O
6. Uses `BlockingCollection<byte[]>` for incoming chunks - efficient take/wait
7. On handshake complete: `NotifyHandshakeComplete()` fires
8. Master secret is copied immediately (cleared by BC after callback)
9. `PrepareSrtpSharedSecret()` derives SRTP keys via RFC 5764
10. SRTP encoders/decoders created from `SrtpTransformEngine`
11. Handshake state tracked via `_handshakeComplete`, `_handshakeFailed`, `_handshaking` flags

**Key details:**
- Timeout: 20 seconds total from start, not per-receive
- Retransmission: DTLS 1.3 exponential backoff (100ms to 6000ms) with random jitter
- **Secure renegotiation override:** `NotifySecureRenegotiation(bool)` overridden to NOT throw on missing renegotiation support - this is required for Pion/other WebRTC libs
- Protocol version: Offers DTLSv12, minimum DTLSv10
- **NO HelloVerifyRequest** - goes straight to handshake

### NEW Stack Handshake

1. `DtlsSrtpTransport.DoHandshake()` delegates to `_connection.DoHandshake()`
2. **Client path:** `DtlsClientProtocol.Connect(this, datagramTransport)` - blocking
3. **Server path:** Uses `DtlsVerifier` for HelloVerifyRequest cookie exchange first, then `serverProtocol.Accept(this, clientDatagramTransport, request)`
4. Transport uses `ConcurrentQueue<byte[]>` with polling sleep loop (25ms intervals)
5. On handshake complete: `OnHandshakeCompleted` event fires
6. Security parameters passed via event args
7. `DtlsSrtpProtocol.CreateMasterKeys()` derives keys using `TlsUtilities.Prf()`
8. `SrtpSessionContext` created with separate encode/decode `SrtpContext` objects

**Key differences:**
- **HelloVerifyRequest was initially ENABLED** in the server - this broke Firefox (Firefox does not support HelloVerifyRequest for WebRTC). It was later disabled for WebRTC.
- Uses `ConcurrentQueue` + `Thread.Sleep(25)` polling instead of `BlockingCollection.TryTake(timeout)` - less efficient, more CPU burn
- No secure renegotiation override - relies on BouncyCastle 2.x default behavior
- Protocol version: DTLSv12 only (DTLSv10 removed, DTLSv13 not yet supported by BC)
- Handshake timeout is passed to BouncyCastle via `GetHandshakeTimeoutMillis()` override

---

## 4. Certificate Handling

### OLD Stack
- **Algorithm:** RSA 2048-bit (hardcoded `DEFAULT_KEY_SIZE = 2048`)
- **Signature:** SHA256WITHRSA
- **Subject:** CN=localhost, Issuer: CN=root
- **SAN:** localhost, 127.0.0.1
- **EKU:** Server Authentication (1.3.6.1.5.5.7.3.1)
- **Validity:** 70 years from now
- **Generation:** `DtlsUtils.CreateSelfSignedTlsCert()` - creates BouncyCastle X509Certificate, wraps in `Org.BouncyCastle.Crypto.Tls.Certificate`
- **Fingerprint:** SHA-256, colon-separated hex

### NEW Stack
- **Default Algorithm:** ECDSA P-256 (secp256r1) - SHA256WITHECDSA
- **RSA option:** RSA 2048-bit available via `useRsa` parameter
- **Subject:** CN=WebRTC (for DTLS-SRTP), CN=DTLS (for base DTLS)
- **No SAN, No EKU** - simpler certificate
- **Validity:** 30 days (NOT 70 years)
- **Generation:** `DtlsCertificateUtils.GenerateECDSACertificate()` - creates BC X509Certificate, wraps in `Org.BouncyCastle.Tls.Certificate` via `crypto.CreateCertificate()`
- **Fingerprint:** SHA-256, colon-separated hex (same format)

### Implications
- ECDSA is the correct direction for WebRTC (per W3C spec recommendation)
- The old RSA-only approach worked because browsers still accept RSA, but some
  implementations (notably older Firefox) had issues with RSA certificates
- The 30-day validity in the new stack is more appropriate for ephemeral WebRTC certs
- **For the fork:** Switch to ECDSA by default while keeping RSA as fallback

---

## 5. SRTP Profile Negotiation

### OLD Stack
- **Client offers:** `SRTP_AES128_CM_HMAC_SHA1_80` only (single profile)
- **Server accepts:** First match from `{_80, _32, NULL_80, NULL_32}`
- **Profile selection:** Server iterates client profiles, picks first recognized one
- **Bug:** Server picks LAST match, not first - if client offers multiple, the lowest
  priority one wins. Confirmed in PR #1486 comments by Lukas Volf.
- **MKI handling:** Client generates 3-byte MKI (due to a bug - divides byte lengths by 8
  again, producing `(16 + 14) / 8 = 3` bytes). Server echoes client's MKI back.

### NEW Stack
- **Client offers 12 profiles** (ordered by preference):
  1. DOUBLE_AEAD_AES_256_GCM_AEAD_AES_256_GCM
  2. DOUBLE_AEAD_AES_128_GCM_AEAD_AES_128_GCM
  3. SRTP_AEAD_AES_256_GCM
  4. SRTP_AEAD_ARIA_256_GCM
  5. SRTP_AEAD_AES_128_GCM
  6. SRTP_AEAD_ARIA_128_GCM
  7. SRTP_ARIA_256_CTR_HMAC_SHA1_80
  8. SRTP_AES128_CM_HMAC_SHA1_80
  9. SRTP_ARIA_128_CTR_HMAC_SHA1_80
  10. SRTP_AES128_CM_HMAC_SHA1_32
  11. SRTP_ARIA_256_CTR_HMAC_SHA1_32
  12. SRTP_ARIA_128_CTR_HMAC_SHA1_32
- **NULL profiles excluded** (per RFC 8827: "MUST NOT negotiate NULL encryption")
- **Server selects:** Highest server-priority profile from mutual set
- **MKI:** Default 0-byte length (no MKI). `ForceDisableMKI` property on server.

### Implications
- Browsers typically support: `SRTP_AES128_CM_HMAC_SHA1_80`, `SRTP_AEAD_AES_128_GCM`, `SRTP_AEAD_AES_256_GCM`
- The old stack's single-profile approach is safe but limiting
- The new stack's ARIA profiles are unusual - most browsers don't support them
- **For the fork:** Offer `SRTP_AEAD_AES_128_GCM`, `SRTP_AEAD_AES_256_GCM`, `SRTP_AES128_CM_HMAC_SHA1_80` in that order. No MKI. No NULL. No ARIA.

---

## 6. Key Derivation

### OLD Stack
- Keys derived in `PrepareSrtpSharedSecret()` called from `NotifyHandshakeComplete()`
- Master secret copied before BC clears it
- Uses `TlsUtilities.PRF()` with label `ExporterLabel.dtls_srtp`
- Seed: `ClientRandom || ServerRandom`
- Length: `2 * (keyLen + saltLen)` where lengths come from `SrtpParameters`
- Layout: `[clientKey | serverKey | clientSalt | serverSalt]`
- Lengths are in BYTES (14-byte salt for AES128_CM)

### NEW Stack
- Keys derived in `DtlsSrtpProtocol.CreateMasterKeys()`
- Called from `OnHandshakeCompleted` event handler
- Uses `TlsUtilities.Prf()` with same label and seed
- **Critical difference:** Lengths are in BITS in `SrtpProtectionProfileConfiguration`, divided by 8 at derivation time
- AES128_CM: key=128 bits (16 bytes), salt=112 bits (14 bytes) - same final values
- Layout: Same `[clientKey | serverKey | clientSalt | serverSalt]` for standard profiles
- **Double AEAD (RFC 8723):** Special layout maintaining inner/outer key separation

### Implications
- The actual key derivation math is equivalent for standard profiles
- The new stack's bit-vs-byte representation is confusing but produces correct results
- **For the fork:** Keep the old stack's byte-based approach for clarity

---

## 7. BouncyCastle API Differences

This is the most significant technical difference and the primary reason a simple upgrade is difficult.

### OLD: `Org.BouncyCastle.Crypto.Tls` (Portable.BouncyCastle 1.9.0)
```
- DefaultTlsClient / DefaultTlsServer (non-generic IDictionary)
- TlsAuthentication.NotifyServerCertificate(Certificate)
- TlsAuthentication.GetClientCredentials(CertificateRequest)
- DefaultTlsSignerCredentials, DefaultTlsEncryptionCredentials
- DtlsClientProtocol(SecureRandom), DtlsServerProtocol(SecureRandom)
- TlsSRTPUtils (SRTP extension utility)
- ProtocolVersion.DTLSv12 / DTLSv10
- byte alertLevel/alertDescription parameters
- Certificate wraps X509CertificateStructure[]
```

### NEW: `Org.BouncyCastle.Tls` (BouncyCastle.Cryptography 2.6.2)
```
- DefaultTlsClient / DefaultTlsServer (generic IDictionary<int, byte[]>)
- TlsAuthentication.NotifyServerCertificate(TlsServerCertificate)
- TlsAuthentication.GetClientCredentials(CertificateRequest) - returns TlsCredentials
- BcDefaultTlsCredentialedSigner (requires TlsCryptoParameters + BcTlsCrypto)
- DtlsClientProtocol() - no SecureRandom parameter
- TlsSrtpUtilities (note: different class name, same purpose)
- ProtocolVersion.DTLSv12.Only() - new API for version selection
- short alertLevel/alertDescription parameters
- Certificate wraps TlsCertificate[] (different type)
- DtlsVerifier for HelloVerifyRequest cookie exchange
- SecurityParameters accessible during handshake completion
- m_context instead of mContext field naming
```

### Migration Path
The old and new BouncyCastle TLS APIs are **NOT compatible**. Every class, interface, and
method signature has changed. A migration from `Org.BouncyCastle.Crypto.Tls` to
`Org.BouncyCastle.Tls` requires rewriting every DTLS file.

**For the fork:** Stay on `Portable.BouncyCastle` if it works. Only migrate to
`BouncyCastle.Cryptography` if a specific feature requires it. If migration is needed,
port the OLD stack's architecture (which is proven) to the new API rather than adopting
SharpSRTP.

---

## 8. Cipher Suite Selection

### OLD Stack
- Client: Inherits from `DefaultTlsClient` - offers whatever BC defaults provide
- Server: Uses `base.GetCipherSuites()` - BC default TLS 1.2 suites
- No explicit cipher suite filtering by certificate type
- Works because RSA certs match RSA cipher suites by default

### NEW Stack
- **Explicit cipher suite lists** based on certificate type:
  - **ECDSA:** `ECDHE_ECDSA_WITH_AES_128_GCM_SHA256`, `..._256_GCM_SHA384`, `..._128_CBC_SHA256`, `..._256_CBC_SHA384`, `..._CHACHA20_POLY1305_SHA256`
  - **RSA:** `ECDHE_RSA_WITH_AES_128_GCM_SHA256`, `..._256_GCM_SHA384`, `..._128_CBC_SHA256`, `..._256_CBC_SHA384`, `..._CHACHA20_POLY1305_SHA256`
- TLS 1.3 suites commented out (BC doesn't support DTLS 1.3 yet)

### Implications
- The new stack's explicit cipher suite lists are cleaner and more correct
- GCM suites should be preferred over CBC for performance
- **For the fork:** Use explicit cipher suite lists matching the certificate type

---

## 9. Error Handling

### OLD Stack
- `OnAlert` event with `AlertLevelsEnum`/`AlertTypesEnum` enums
- Detailed logging of alert descriptions
- `close_notify` treated as info, everything else as warning
- `TlsFatalAlert` caught and error message extracted
- Timeout tracked internally with remaining-time calculation
- **NotifySecureRenegotiation override** - gracefully handles clients without renegotiation (required for Pion compatibility)

### NEW Stack
- `OnAlert` event with `TlsAlertLevelsEnum`/`TlsAlertTypesEnum` enums (more comprehensive enum with IANA assignments)
- Logging via custom `Log` class bridged to SIPSorcery's `ILogger`
- Debug logging gated behind `Log.DebugEnabled`
- Exception-based error flow (no error codes, just exception messages)
- Timeout delegated to BouncyCastle via `GetHandshakeTimeoutMillis()`
- **NO NotifySecureRenegotiation override** - relies on BC 2.x default

---

## 10. Known Bugs and Issues in the NEW Stack

These were discovered during PR #1486 review and subsequent PRs:

### Bug 1: GCM cipher reuse crash (Fixed in PR)
- **Symptom:** `System.InvalidOperationException: GCM cipher cannot be reused for encryption`
- **Cause:** Multiple media streams (audio + video) sharing the same SRTP encryption context without locking
- **Fix:** Added locking when RTP/RTCP multiplexing shares the rtpChannel

### Bug 2: Firefox HelloVerifyRequest failure (Fixed in PR)
- **Symptom:** DTLS handshake fails with Firefox
- **Cause:** DtlsServer used `DtlsVerifier` for HelloVerifyRequest cookie exchange. Firefox does not support HelloVerifyRequest for WebRTC.
- **Reference:** https://github.com/w3c/webrtc-pc/issues/749
- **Fix:** Disabled HelloVerifyRequest for WebRTC

### Bug 3: MKI negotiation causes auth failures (Fixed via PR #1489)
- **Symptom:** `SRTP unprotect failed, result -1` when connecting to old SIPSorcery
- **Cause:** Old stack had a bug generating 3-byte MKI (dividing byte lengths by 8 again). New stack correctly handles MKI but old stack's Unprotect does not strip MKI bytes from authenticated portion.
- **Fix:** Disable MKI entirely for WebRTC per RFC 8827: "An SRTP Master Key Identifier (MKI) MUST NOT be used."

### Bug 4: NULL cipher suite selection (Fixed in PR)
- **Symptom:** NULL encryption negotiated when it should not be
- **Cause:** Old stack's `ProcessClientExtensions` picked the LAST matching profile instead of the FIRST. When SharpSRTP offered NULL profiles, they got selected.
- **Fix:** Removed NULL profiles from offered list. RFC 8827: "MUST NOT negotiate NULL encryption modes."

### Bug 5: SRTCP replay protection false positive (Fixed in PR)
- **Symptom:** `SRTCP unprotect failed for audio track, result -4` (replay)
- **Cause:** With multiplexed audio/video, RTCP indexes for both start from 1 but share the same SrtpContext, causing replay detection false positives.
- **Fix:** Per-SSRC context tracking

### Bug 6: Receiver ROC never updates - video freezes after ~5 min (Fixed in PR #1499)
- **Symptom:** `SRTP unprotect failed for video, result -3` after ~65535 packets
- **Cause:** `context.Roc` only incremented in `ProtectRtp` (send path), never in `UnprotectRtp` (receive path). After RTP sequence number wraps, HMAC computed with wrong ROC.
- **Fix:** Derive ROC from per-SSRC last accepted index before HMAC verification

### Bug 7: Infinite server handshake timeout (Fixed in PR #1495)
- **Symptom:** DtlsServer handshake hangs forever
- **Cause:** Default handshake timeout was infinite
- **Fix:** Set default timeout to 20 seconds

### Bug 8: aiortc hash algorithm rejection (Fixed in PR)
- **Symptom:** DTLS handshake fails with aiortc (Python WebRTC)
- **Cause:** `IsHashSupported` only returned true for SHA-256, but aiortc uses SHA-512
- **Fix:** Use BouncyCastle's `DigestUtilities.GetDigest()` to check support dynamically

### Bug 9: SrtcpCryptoContext.ComputeIv crash on close (Observed, unclear if fixed)
- **Symptom:** `System.IndexOutOfRangeException` in `SrtcpCryptoContext.ComputeIv` when closing connection
- **Cause:** SRTCP context accessed after transport close, null/empty key state

---

## 11. Interoperability Test Results from PR #1486

Tested by Aaron Clauson (sipsorcery maintainer) using Docker echo servers:

### Working with NEW Stack (after all fixes):
- Janus WebRTC
- libdatachannel
- libwebrtc (Chrome's lib, ~12 months old image)
- Pion WebRTC
- webrtc-rs
- werift WebRTC
- aiortc (after SHA-512 fix)
- Firefox (after HelloVerifyRequest disabled)
- Chrome/Edge browsers

### Still Problematic:
- **sipsorcery master as server / SharpSRTP as client:** Auth failures due to MKI bug in old code
- **kurento:** Requires specific client switch not wired up
- **gstreamer:** Failed (no details on cause)

---

## 12. SRTP Implementation Comparison

### OLD Stack (Transform/ directory)
- Ported from Jitsi SRTP (Java) via RestComm/media-core
- `SrtpCryptoContext` / `SrtcpCryptoContext` - stateful per-SSRC contexts
- `SrtpTransformEngine` - creates RTP/RTCP transformers
- `IPacketTransformer` interface with `Transform()` / `ReverseTransform()`
- `RawPacket` wrapper for packet manipulation
- Ciphers: AES-CM (CTR mode), AES-F8
- Auth: HMAC-SHA1
- **No AEAD support** (no AES-GCM)
- Thread safety: Callers lock on encoder/decoder objects

### NEW Stack (Lib/SRTP/ directory)
- Written from scratch based on RFCs
- `SrtpContext` - unified context for both RTP and RTCP
- `SrtpSessionContext` - groups encode/decode contexts
- `ISrtpContext` interface with `ProtectRtp()` / `UnprotectRtp()` etc.
- Direct byte array manipulation (no RawPacket wrapper)
- Ciphers: AES-CM, AES-F8, **AEAD AES-GCM**, AEAD ARIA-GCM, **Double AEAD**
- Auth: HMAC-SHA1, **NONE** (for AEAD modes with built-in auth)
- Per-SSRC replay window tracking
- Thread safety: Locking added post-merge for multiplexed streams

### Key Advantage of NEW SRTP
The AEAD (AES-GCM) support is the single genuinely valuable addition. Modern browsers
prefer GCM cipher suites and will negotiate them when available. The old stack only
supports AES-CM with HMAC-SHA1, which works but is not optimal.

---

## 13. What to Keep vs Discard

### KEEP from OLD Stack
1. **DtlsSrtpTransport architecture** - proven, efficient (BlockingCollection vs polling)
2. **DtlsSrtpClient/Server structure** - clean, simple, works with browsers
3. **NotifySecureRenegotiation override** on server - essential for Pion compatibility
4. **Key derivation in NotifyHandshakeComplete** - correct timing, proven approach
5. **SrtpTransformEngine / IPacketTransformer pattern** - well-tested
6. **DtlsUtils credential loading** - comprehensive and correct

### UPGRADE from OLD Stack
1. **Certificate generation** - switch from RSA to ECDSA P-256 (use new stack's approach)
2. **SRTP profile list** - offer GCM profiles, remove NULL, fix server selection order
3. **MKI handling** - set to empty byte array per RFC 8827
4. **Certificate validity** - reduce from 70 years to 30 days

### TAKE from NEW Stack (selectively)
1. **AEAD encryption** (`Lib/SRTP/Encryption/AEAD.cs`) - for AES-GCM support
2. **Extended protection profiles** - `SrtpProtectionProfileConfiguration` concept
3. **Per-SSRC replay window** - better than old stack's per-context approach
4. **ECDSA certificate generation** - `DtlsCertificateUtils.GenerateECDSACertificate()`
5. **ForceDisableMKI** pattern - clean way to enforce RFC 8827

### DISCARD from NEW Stack
1. **SharpSRTP's DtlsClient/DtlsServer** - over-engineered, event-driven, not needed
2. **DtlsVerifier / HelloVerifyRequest** - not supported by Firefox for WebRTC
3. **ConcurrentQueue + Thread.Sleep polling** - worse than BlockingCollection
4. **Lib/DTLSSRTP layer** - unnecessary abstraction, adds complexity
5. **Double AEAD support** - browsers don't use it, adds complexity
6. **ARIA cipher support** - browsers don't support it

---

## 14. BouncyCastle Migration Decision

### Option A: Stay on Portable.BouncyCastle 1.9.0
- **Pro:** Zero migration work, proven working
- **Con:** Old library, no new features, may have security fixes missing
- **Con:** `Org.BouncyCastle.Crypto.Tls` namespace is deprecated

### Option B: Migrate to BouncyCastle.Cryptography 2.x
- **Pro:** Active development, security fixes, ECDSA support improved
- **Pro:** `Org.BouncyCastle.Tls` namespace is the future
- **Con:** Every DTLS class must be rewritten to new API
- **Con:** Risk of introducing new bugs during migration

### Option C: Use BouncyCastle.Cryptography 2.x but keep old architecture
- **Pro:** Best of both worlds - new crypto, proven DTLS flow
- **Con:** Most work - port old design to new API
- **Con:** Old `DefaultTlsClient`/`DefaultTlsServer` patterns need updating to new signatures

### Recommendation
**Option A for now** (stay on Portable.BouncyCastle 1.9.0), with targeted cherry-picks:
- Add ECDSA cert generation using BouncyCastle's existing EC key gen (which IS in 1.9.0)
- Fix the SRTP profile selection order bug
- Remove MKI negotiation
- Add explicit cipher suite lists

Only migrate to BouncyCastle.Cryptography 2.x if a browser stops supporting the cipher
suites available with the old stack, or if a security vulnerability is found in 1.9.0.

---

## 15. Fork Plan Summary

1. Start from RTLink's SIPSorcery v6.0.11 bundled copy
2. Fix SRTP profile selection (first match, not last)
3. Generate ECDSA certs instead of RSA (BC 1.9.0 supports this)
4. Remove MKI negotiation (set empty byte array)
5. Add SRTP_AEAD_AES_128_GCM and SRTP_AEAD_AES_256_GCM to offered profiles
6. Port AEAD encryption from SharpSRTP for GCM support
7. Add per-SSRC replay window tracking
8. Reduce cert validity to 30 days
9. Keep NotifySecureRenegotiation override
10. Keep BlockingCollection transport
11. Test against Chrome, Firefox, Edge, Pion, libdatachannel

---

## References

- [PR #1486: New DTLS-SRTP implementation based upon SharpSRTP](https://github.com/sipsorcery-org/sipsorcery/pull/1486)
- [PR #1495: DTLS-SRTP code cleanup and fix for infinite DtlsServer handshake timeout](https://github.com/sipsorcery-org/sipsorcery/pull/1495)
- [PR #1499: SRTP unprotect failed after ~4-5 min: receiver ROC never updates](https://github.com/sipsorcery-org/sipsorcery/pull/1499)
- [PR #1555: SRTP Performance improvements and bugfixes](https://github.com/sipsorcery-org/sipsorcery/pull/1555)
- [SharpSRTP Repository](https://github.com/jimm98y/SharpSRTP)
- [RFC 5764: DTLS-SRTP](https://datatracker.ietf.org/doc/html/rfc5764)
- [RFC 7714: AES-GCM for SRTP](https://datatracker.ietf.org/doc/html/rfc7714)
- [RFC 8827: WebRTC Security Architecture](https://datatracker.ietf.org/doc/html/rfc8827)
- [RFC 8723: Double AEAD](https://www.rfc-editor.org/rfc/rfc8723.txt)
- [Issue #1104: Chrome 124 DTLS Handshake Break](https://github.com/sipsorcery-org/sipsorcery/issues/1104)
- [W3C WebRTC HelloVerifyRequest Discussion](https://github.com/w3c/webrtc-pc/issues/749)
