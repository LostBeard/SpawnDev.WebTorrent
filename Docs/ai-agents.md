# AI Agent Communication via DHT

SpawnDev.WebTorrent includes a decentralized pub/sub system for AI agent communication, built on BEP 46 (DHT Mutable Items).

## Overview

Each AI agent has a cryptographic identity (Ed25519 key pair). Agents publish state updates to the BitTorrent DHT — a decentralized key-value store with 15+ million nodes. Other agents subscribe by public key and receive updates automatically.

No central server. No database. No cloud account. Just the DHT.

## Quick Start

```csharp
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Discovery;

// Create DHT + agent channel
var dht = new DhtDiscovery();
await dht.StartAsync(infoHash, 6881);

var agent = new AgentChannel(dht);
// Optional: real Ed25519 signing via SpawnDev.BlazorJS.Cryptography
// await agent.InitAsync(crypto);

Console.WriteLine($"Agent ID: {agent.PublicKeyHex}");
```

## Publish State

```csharp
// Publish arbitrary state (max 1000 bytes per BEP 44)
await agent.PublishStateAsync(myStateBytes);

// Publish on a named channel
var weights = agent.Channel("weights");
await weights.PublishAsync(weightUpdateBytes);

// Publish a torrent info hash (other agents auto-discover + download)
await agent.PublishTorrentAsync(modelInfoHash);
```

## Subscribe to Agent Updates

```csharp
// Subscribe to another agent's updates
agent.OnAgentUpdate += (publicKey, value, sequence) =>
{
    Console.WriteLine($"Agent {Convert.ToHexString(publicKey)[..8]} published seq {sequence}");
    // value could be an info hash → download the torrent
};

await agent.SubscribeAsync(otherAgentPublicKey);
```

## Named Channels

Scope pub/sub to specific topics:

```csharp
var weights = agent.Channel("weights");      // Model weight updates
var cache = agent.Channel("kv-cache");       // KV cache slices
var control = agent.Channel("control");      // Coordination messages
var gradients = agent.Channel("gradients");  // Federated learning

await weights.PublishTorrentAsync(newModelHash);
await cache.PublishAsync(myCacheSlice);
await control.PublishAsync(coordinationMessage);
```

## Use Cases

### 1. Live Model Weight Updates

Training node publishes updated weights as a torrent. Inference nodes watch the key and auto-download:

```
Training Node:
  1. Train batch → new weights
  2. client.SeedAsync(weights) → info hash
  3. agent.PublishTorrentAsync(infoHash)

Inference Node:
  agent.OnAgentUpdate += (key, hash, seq) =>
  {
      // New weights available! Download via P2P
      var swarm = await client.AddAsync(infoHashMagnet);
  };
```

### 2. Distributed KV Cache

Each device publishes its KV cache slice. Others discover and download:

```
Device A: cache.PublishAsync(mySlice, "layer-0")
Device B: cache.PublishAsync(mySlice, "layer-0")
Device C: cache.SubscribeAsync(deviceA.PublicKey, "layer-0")
          cache.SubscribeAsync(deviceB.PublicKey, "layer-0")
```

### 3. Federated Learning

Participants publish gradient updates. Aggregator collects them:

```
Participant: gradients.PublishAsync(myGradients)
Aggregator:  foreach (var p in participants)
                 gradients.SubscribeAsync(p.PublicKey);
             OnAgentUpdate += aggregate;
```

### 4. Agent Discovery

Agents announce themselves via DHT. Others discover by searching:

```
agent.PublishStateAsync(myCapabilities);  // "I can do image classification"
// Other agents find you by your public key
```

## Signing

The `IDhtSigner` interface supports pluggable signing algorithms:

| Signer | Algorithm | Platform | Status |
|--------|-----------|----------|--------|
| `Ed25519Signer` | Ed25519 | Both | BEP 44 compliant, via SpawnDev.BlazorJS.Cryptography 3.1.0 |
| `HmacFallbackSigner` | HMAC-SHA512 | Both | Testing only |

```csharp
// Use real crypto (browser + desktop)
var signer = new Ed25519Signer(crypto);
await signer.GenerateKeyAsync();

// Export for persistence
var (pub, priv) = await signer.ExportKeyPairAsync();
// Save to disk / OPFS / IndexedDB
```

## Relationship to AcceleratorType.P2P

This communication layer is the foundation for SpawnDev.ILGPU's planned 7th backend — `AcceleratorType.P2P`. The P2P accelerator will:

1. Discover available GPUs across devices via `AgentChannel`
2. Distribute kernel workgroups to remote peers
3. Transfer input buffers via torrent pieces
4. Collect results over WebRTC data channels
5. Present as a single logical accelerator to the application

Same C# kernel code. 1 GPU or 10 GPUs. Transparent.
