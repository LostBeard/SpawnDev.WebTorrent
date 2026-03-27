# SpawnDev.WebTorrent — Post-1.0 Roadmap

## Distributed Compute via P2P Network

The WebTorrent P2P network we're building for model delivery creates a natural foundation for **distributed compute sharing across devices**. Every connected browser/desktop is already exchanging data over WebRTC — extending this to share compute workloads is a natural evolution.

### Vision: Peer-to-Peer GPU Compute

- **Model inference sharding** — Split a large model across multiple browser peers. Each peer runs inference on their portion (using SpawnDev.ILGPU), passes intermediate tensors to the next peer via WebRTC data channels. A 14B model that doesn't fit on one device runs across 4 devices with 4GB each.

- **Parallel batch processing** — Distribute inference batches across peers. One client sends images to N peers, each runs classification, results aggregate back. Linear speedup for embarrassingly parallel workloads.

- **Training data parallelism** — Distributed training across browser peers. Each peer computes gradients on their local data, gradients are aggregated via the P2P network (all-reduce over WebRTC). Privacy-preserving: raw data never leaves the device.

- **Volunteer compute pools** — Users opt in to donate idle GPU time. Like Folding@Home but for ML inference in the browser. The "AI Assistant" demo could distribute inference across volunteering peers when the local device is underpowered.

### Architecture Extension

The wire protocol extension system (BEP 10) already supports custom message types:

```
sd_compute extension:
  - TASK_OFFER:   "I need compute, here's the workload spec"
  - TASK_ACCEPT:  "I'll take that workload"
  - TENSOR_SEND:  "Here's the input tensor for your shard"
  - TENSOR_RECV:  "Here's my output tensor"
  - HEARTBEAT:    "Still computing, ETA X ms"
  - RESULT:       "Computation complete"
```

This rides the same WebRTC connections used for piece exchange — no new infrastructure needed.

### Prerequisites

- SpawnDev.WebTorrent 1.0 (P2P connectivity proven)
- SpawnDev.ILGPU.ML (GPU inference engine)
- SpawnDev.BlazorJS (browser API access)
- Wire protocol extension framework (already built)
- Tensor serialization over data channels

### Security Considerations

- Compute tasks must be verifiable (hash of expected output)
- Malicious peers could return garbage tensors — need redundant compute + voting
- Privacy: intermediate tensors may leak information about the input
- Bandwidth: tensor transfer must be smaller than the compute savings

## Other Post-1.0 Features

### BEP 52 (BitTorrent v2)
- SHA-256 piece hashes (stronger integrity for weight files)
- Per-file Merkle trees (verify individual file chunks without full piece)
- Better suited for random-access streaming pattern

### SpawnDev.WebFS Integration
- Virtual filesystem backed by torrent swarm
- Mount a torrent as a drive (Dokan on desktop, OPFS in browser)
- File access triggers piece downloads transparently

### Pure C# DHT (Kademlia)
- Decentralized peer discovery without tracker dependency
- Eliminates single point of failure
- Required for truly serverless P2P

### AI Agent Communication Protocol
- Custom wire extension for multi-agent coordination
- Agents discover each other via the torrent swarm
- Task distribution, result aggregation, consensus protocols
- Enables browser-based AI agent swarms

### GPU-Accelerated Piece Hashing
- Use SpawnDev.ILGPU for parallel SHA-1/SHA-256 computation
- Batch-verify thousands of pieces on GPU
- Significant speedup for large model verification (2GB+ files)

### Bandwidth-Aware Seeding
- Detect metered connections (mobile data)
- Respect upload limits (configurable, default conservative)
- Smart seeding: prioritize pieces that are rare in the swarm
