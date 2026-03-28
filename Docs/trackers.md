# Tracker Configuration

## Default Trackers

SpawnDev.WebTorrent ships with these default WebSocket trackers, tried in order:

| # | Tracker | Notes |
|---|---------|-------|
| 1 | `wss://hub.spawndev.com:44365/announce` | Our own tracker (SpawnDev.WebTorrent.Server) |
| 2 | `wss://tracker.openwebtorrent.com` | OpenWebTorrent project |
| 3 | `wss://tracker.files.fm:7073/announce` | Files.fm public tracker |
| 4 | `wss://tracker.ghostchu-services.top:443/announce` | GhostChu community tracker |

Our tracker (`hub.spawndev.com`) is listed first so SpawnDev clients always find each other. Public trackers provide broader swarm access.

## Configuring Trackers

```csharp
var client = new WebTorrentClient(new WebTorrentOptions
{
    Trackers = new[]
    {
        "wss://hub.spawndev.com:44365/announce",
        "wss://tracker.openwebtorrent.com",
        // Add your own trackers here
    }
});
```

Per-torrent trackers from magnet URIs (`&tr=` parameters) and `.torrent` announce lists are used in addition to the client defaults.

## Running Your Own Tracker

SpawnDev.WebTorrent.Server includes a full WebSocket tracker:

```csharp
var tracker = new TorrentTracker();
app.UseWebSockets();
app.MapWebTorrentServer(tracker);
// Tracker is now live at /announce
```

The tracker handles:
- **Announce** — Peer registration and swarm membership
- **Offer/Answer relay** — WebRTC signaling for browser-to-browser P2P
- **Stats** — `/stats` endpoint for monitoring swarm health

## Tracker Protocol

The tracker uses JSON messages over WebSocket, compatible with the WebTorrent tracker protocol:

### Client -> Tracker
```json
{ "action": "announce", "info_hash": "hex...", "peer_id": "hex...", "uploaded": 0, "downloaded": 0, "left": 0 }
{ "action": "offer", "to_peer_id": "hex...", "offer": { "type": "offer", "sdp": "..." }, "offer_id": "..." }
{ "action": "answer", "to_peer_id": "hex...", "answer": { "type": "answer", "sdp": "..." }, "offer_id": "..." }
```

### Tracker -> Client
```json
{ "action": "announce", "complete": 5, "incomplete": 2, "peers": [{ "peer_id": "hex..." }] }
{ "action": "offer", "peer_id": "hex...", "offer": { ... }, "offer_id": "..." }
{ "action": "answer", "peer_id": "hex...", "answer": { ... }, "offer_id": "..." }
```

## Known Dead Trackers (as of 2026-03-27)

These were previously in the default list but are no longer operational:

| Tracker | Issue | Removed |
|---------|-------|---------|
| `wss://tracker.webtorrent.dev` | SSL certificate expired | 2026-03-27 |
| `wss://tracker.btorrent.xyz` | Connection timeout | 2026-03-27 |

## Health Checking

You can verify tracker availability with curl (400/404 responses are normal — these are WebSocket endpoints):

```bash
curl -sS --max-time 5 -o /dev/null -w "%{http_code}" "https://hub.spawndev.com:44365/announce"
# 400 = server up, rejects non-WebSocket HTTP (correct behavior)
```
