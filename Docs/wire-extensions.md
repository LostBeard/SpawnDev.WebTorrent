# Wire Extensions (BEP 10)

SpawnDev.WebTorrent supports custom wire protocol extensions using the BEP 10 Extension Protocol. This lets you build custom P2P protocols on top of the BitTorrent wire — distributed compute, AI agent communication, file synchronization, or anything else.

## The Pattern

Same as JS WebTorrent's `wire.use()` — register a factory function that creates a fresh extension instance for each peer connection. The factory receives the swarm and wire, matching JS WebTorrent's `IExtensionFactory.CreateExtension(torrent, wire)`:

```csharp
// Client-wide — all swarms, all peers
client.UseExtension((swarm, wire) => new MyExtension());

// Single swarm — all peers on this torrent
swarm.UseExtension((swarm, wire) => new MyExtension());
```

Extensions are created **before** the BEP 10 handshake, so they participate in extension negotiation. After the handshake:
- `ext.IsSupported` — `true` if the remote peer also has this extension
- `ext.RemoteId` — the peer's extension ID (used for sending messages)
- `ext.LocalId` — our extension ID (assigned automatically)

## Creating an Extension

Extend `WireExtension`:

```csharp
using SpawnDev.WebTorrent.Wire;

public class SdComputeExtension : WireExtension
{
    public override string Name => "sd_compute";

    // Fired when peer sends us a message with our extension ID
    public override async Task HandleMessageAsync(byte[] payload)
    {
        var msg = MyProtocol.Decode(payload);
        OnMessage?.Invoke(msg);
    }

    // Include data in BEP 10 handshake (optional)
    public override Dictionary<string, object>? GetHandshakeData()
    {
        return new()
        {
            ["capabilities"] = new List<object> { "gpu", "wasm" },
            ["version"] = 1L,
        };
    }

    // Process peer's handshake data (optional)
    public override void ProcessHandshakeData(Dictionary<string, object> data)
    {
        if (data.TryGetValue("capabilities", out var caps) && caps is List<object> capList)
        {
            PeerCapabilities = capList.OfType<byte[]>()
                .Select(b => System.Text.Encoding.UTF8.GetString(b)).ToArray();
        }
    }

    public string[] PeerCapabilities { get; private set; } = Array.Empty<string>();
    public event Action<object>? OnMessage;
}
```

## Sending Messages

Extensions can send directly via the built-in `SendAsync` method — no event wiring needed:

```csharp
// Inside your extension
public async Task SendComputeTask(byte[] taskData)
{
    if (!IsSupported) return; // peer doesn't have this extension
    await SendAsync(taskData); // sends via wire.SendExtensionMessageAsync(RemoteId, data)
}
```

`SendAsync` uses the extension's `Manager.Wire` reference, which is set automatically when the extension is registered. This matches JS WebTorrent's `Extension.Send()` → `Wire.Extended()` pattern.

## Accessing Extensions on Connected Peers

After a peer connects, access their extension via the wire's ExtensionManager:

```csharp
swarm.OnPeerConnect += (peer) =>
{
    var ext = peer.Wire.Extensions.Get<SdComputeExtension>();
    if (ext?.IsSupported == true)
    {
        Console.WriteLine($"Peer supports sd_compute!");
        _ = ext.SendComputeTask(myTaskData);
    }
};
```

## Extension Flow

```
1. client.UseExtension((swarm, wire) => new MyExt())
   └─ stored in client._extensionFactories

2. client.AddAsync("magnet:...")
   └─ WireSwarmEvents(swarm) applies factories to new swarm
   └─ swarm._extensionFactories

3. Peer connects via tracker/WebRTC
   └─ PeerCoordinator.SetupPeerAsync(conn)
       ├─ wire = new WireProtocol(conn)
       ├─ wire.Extensions.Register(factory(swarm, wire))  ← extension created HERE
       ├─ wire.SendHandshakeAsync(...)                     ← extension in BEP 10 negotiation
       ├─ wire.ReceiveHandshakeAsync(...)                  ← RemoteId set, IsSupported = true
       └─ OnPeerConnected fires
```

## Built-in Extensions

| Extension | BEP | Purpose |
|-----------|-----|---------|
| `UtMetadataExtension` | 9 | Exchange torrent metadata with peers (auto-registered) |
| `UtPexExtension` | 11 | Peer exchange — share known peers (auto-registered) |

Both are registered automatically on every wire via `UseExtension` in the `TorrentSwarm` constructor.

## Notes

- Factory receives `(TorrentSwarm swarm, WireProtocol wire)` — use these to configure the extension
- Factory creates a **new instance per peer** — don't share state between instances unless thread-safe
- Extensions registered after peers are already connected won't apply to those peers
- Register extensions before calling `AddAsync` or `SeedAsync` for best results
- The `Name` property must match on both sides for negotiation to succeed
- Use `SendAsync` to send — no external event wiring needed
- Never use `Console.Error.WriteLine` in Blazor WASM — use `Console.WriteLine`
