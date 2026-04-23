# SpawnDev.WebTorrent — Post-1.1 Roadmap

## Completed in v1.1.0

These were originally on the roadmap and are now **shipped**:

- **Pure C# DHT (Kademlia)** — Full routing table, KRPC protocol, bootstrap, iterative lookup, get_peers, announce_peer. 160 k-buckets, K=8.
- **AI Agent Communication** — BEP 46 DHT mutable items with Ed25519 signing. AgentChannel high-level pub/sub. Named channels. Works in browser and desktop via SpawnDev.BlazorJS.Cryptography.
- **SwarmCompute Foundation** — PublishTaskAsync, JoinAsWorkerAsync, SubmitResultAsync. Task distribution framework over WebRTC.
- **Wire Protocol Extensions** — BEP 10 extension framework, ut_metadata (BEP 9), ut_pex (BEP 11), sd_compute message types.

## Distributed Compute via P2P Network (4.0.0 — Active Development)

Moved into SpawnDev.ILGPU.ML 4.0.0 scope. See `D:\users\tj\Projects\SpawnDev.ILGPU.ML\Plans\v4.0.0-checklist.md`.

### Architecture: sd_compute Wire Extension

```
sd_compute extension:
  - TASK_OFFER:   "I need compute, here's the workload spec"
  - TASK_ACCEPT:  "I'll take that workload"
  - TENSOR_SEND:  "Here's the input tensor for your shard"
  - TENSOR_RECV:  "Here's my output tensor"
  - HEARTBEAT:    "Still computing, ETA X ms"
  - RESULT:       "Computation complete"
```

Rides the same WebRTC connections used for piece exchange — no new infrastructure needed.

### Key Features (in 4.0.0 checklist)
- **Model inference sharding** — 14B model across 4 devices with 4GB each
- **Parallel batch processing** — Linear speedup for parallel workloads
- **Training data parallelism** — All-reduce over WebRTC, privacy-preserving
- **Volunteer compute pools** — Folding@Home for ML inference
- **AcceleratorType.P2P** — 7th SpawnDev.ILGPU backend, transparent distribution
- **BEP 46 shared memory** — KV cache, model weights, coordination over DHT

### Security Considerations
- Compute tasks must be verifiable (hash of expected output)
- Malicious peers could return garbage tensors — need redundant compute + voting
- Privacy: intermediate tensors may leak information about the input
- Bandwidth: tensor transfer must be smaller than the compute savings

## Future Features (Post-4.0.0)

### BEP 52 (BitTorrent v2) — SHIPPED
- **SHA-256 piece hashes** — SHIPPED in 3.1.0-rc.3 (commit `de92f8d`, 2026-04-22). `TorrentCreator` defaults to SHA-256; verify hot path branches on hash length; `TorrentMetadata.PieceHashAlgorithm` surfaces the algorithm.
- **Per-file Merkle trees** (verify individual file chunks without full piece) — SHIPPED in 3.1.2 stable (2026-04-22). `MerkleHasher`, `IncrementalMerkleHasher`, `MerkleProofVerifier`, `MerkleProofBuilder`. Piece-layer lookup via file root.
- **Hybrid v1+v2 info dict** for backwards compatibility — SHIPPED in 3.1.2 (`TorrentCreatorOptions.Hybrid = true`). Both SHA-1 + SHA-256 infohashes; pad files inserted for per-file piece alignment in multi-file hybrid.
- **Parse v2 info dicts from external clients** — SHIPPED in 3.1.2 (parser) + 3.1.3-rc.2 (pure-v2 multi-file parser gap fixed). JS WebTorrent interop verified by Captain via `hub.spawndev.com`.
- **Multihash magnet URI (`urn:btmh:`)** — SHIPPED in 3.1.2 (parse + emit in `ComputedMagnetUri`).
- **Peer-wire extension** (messages 21/22/23) — SHIPPED in 3.1.2. `Bep52WireMessages` codecs + `V2HashRequestCoordinator` state machine + `Torrent.OnV2HashRequest` seed path + `RequestV2HashesAsync` client path.
- **Remaining:** libtorrent / qBittorrent cross-client v2-peer-wire interop verification (manual; runbook in `Plans/PLAN-BEP52-External-Interop.md`). Not blocking production.
- See `Plans/bep52-phase2-execution.md` + `Docs/bep52.md` for the full end-state.

### SpawnDev.WebFS Integration
- Virtual filesystem backed by torrent swarm
- Mount a torrent as a drive (Dokan on desktop, OPFS in browser)
- File access triggers piece downloads transparently

### GPU-Accelerated Piece Hashing
- Use SpawnDev.ILGPU for parallel SHA-1/SHA-256 computation
- Batch-verify thousands of pieces on GPU
- Significant speedup for large model verification (2GB+ files)

### Bandwidth-Aware Seeding
- Detect metered connections (mobile data)
- Respect upload limits (configurable, default conservative)
- Smart seeding: prioritize pieces that are rare in the swarm

### Push Notifications for Compute Swarms
- Web Push API to recall opted-in volunteers when swarm capacity is low
- Service worker stays registered after tab closes — no app install needed
- Coordinator detects low capacity → publishes "help wanted" via BEP 46
- Server picks up the signal → fires push to opted-in devices
- User taps notification → browser opens join link → auto-joins swarm
- Consent levels: "always notify", "this swarm only", "never"
- Works on Android Chrome without any app install

### Compute Request Board (hub.spawndev.com)
- Server endpoint: `POST /compute/request` — coordinator posts "looking for compute nodes"
- Server endpoint: `GET /compute/requests` — volunteers browse available swarms
- Request includes: swarm name, owner, purpose, estimated duration, TFLOPS needed, join link
- Volunteers click join link → auto-joins the swarm
- Requests expire after configurable TTL (default 1 hour)
- Server relays push notifications to opted-in volunteers matching the request
- WebSocket feed for real-time request updates
- Public API — any app can post/browse compute requests
- Privacy: coordinator chooses what to disclose (name only, or full details)

### Compute Swarm Consent & Trust
- Join consent flow: "Always join" / "Join this time" / "Not now"
- Per-origin trust saved in localStorage
- Family devices auto-join trusted swarms silently
- Unknown swarms show swarm name, owner, purpose before asking
- Coordinator can kick/block misbehaving peers
