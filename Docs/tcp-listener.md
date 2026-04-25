# TCP Listener — Accepting Inbound BitTorrent Peers

`TcpListenerService` lets a desktop `WebTorrentClient` accept inbound BitTorrent peer-wire connections on a TCP port. With this enabled, mainline clients (qBittorrent, libtorrent, Transmission, rqbit) can dial into your seeder by IP+port and leech the torrents you're hosting.

Shipped in **SpawnDev.WebTorrent 3.1.7**. Closes the seed-C# / leech-mainline interop direction in `PLAN-BEP52-External-Interop.md` Step 4.

## Why this exists

`TcpPeer.ConnectAsync` only handles the outbound case - we initiate to a known peer. For full BEP 3 interoperability with the rest of the BitTorrent ecosystem we also need to accept inbound connections. A remote peer that learned our IP+port from a tracker, DHT, PEX, or local service discovery will dial us first. Without a listener, we are leech-only outside the WebRTC swarm.

The browser cannot bind a listening socket - this API is desktop-only. Browser-side WebTorrentClient instances ignore `TcpListenPort` silently.

## API surface

### `WebTorrentClientOptions`

| Property | Type | Default | Meaning |
|---------|------|---------|---------|
| `TcpListenPort` | `int?` | `null` | `null` = no listener (back-compat). `0` = kernel-assigned ephemeral port. `>0` = bind that specific port. |
| `TcpListenAddress` | `IPAddress?` | `IPAddress.Any` | Local address to bind. Use `IPAddress.Loopback` for localhost-only test harnesses; leave default for production deployments where external peers need to reach you. |

### `WebTorrentClient`

| Member | Description |
|--------|-------------|
| `WebTorrentClient.TcpListener` | `TcpListenerService?` — the running listener, or `null` if none. Inspect `.LocalEndPoint.Port` to learn the kernel-assigned port when `TcpListenPort = 0`. |
| `WebTorrentClient.EnsureTcpListenerAsync(int port = 0, IPAddress? address = null)` | Idempotent - binds + starts the listener if not already running. Use this for explicit start-on-demand or when you need to await the actual port assignment. |

### `TcpListenerService` (rarely constructed directly)

| Member | Description |
|--------|-------------|
| `LocalEndPoint` | `IPEndPoint` — actual bound address+port. |
| `AcceptedCount` | `int` — number of inbound BT handshakes that matched a torrent and were attached to the swarm. |
| `RejectedCount` | `int` — connections dropped because the BitTorrent handshake was malformed or the info_hash didn't match any torrent in the client. |
| `OnLog` | `event Action<string>?` — per-event diagnostic callback. Wire this up to your logger to see accept / reject decisions. |

You don't need to `new` this directly - `WebTorrentClient` owns the lifetime via `EnsureTcpListenerAsync` / the `TcpListenPort` option.

## Quickstart

### One-liner: enable inbound + auto-bind ephemeral port

```csharp
using System.Net;
using SpawnDev.WebTorrent;

await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    TcpListenPort = 0,                  // 0 = kernel picks a free port
    TcpListenAddress = IPAddress.Any,   // default; bind on every interface
});

// Constructor fires EnsureTcpListenerAsync fire-and-forget. If you need the
// actual port (e.g. to advertise it), await the explicit call - it's idempotent.
await client.EnsureTcpListenerAsync(0, IPAddress.Any);

int port = client.TcpListener!.LocalEndPoint.Port;
Console.WriteLine($"Inbound BT peers can dial 0.0.0.0:{port}");
```

### Bind a specific port (e.g. for static port forwarding)

```csharp
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    TcpListenPort = 51413,              // classic mainline default
});
```

### Localhost-only (test harness pattern)

```csharp
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    TcpListenPort = 0,
    TcpListenAddress = IPAddress.Loopback,
});
```

### Hook diagnostics

```csharp
client.TcpListener!.OnLog += msg => Console.WriteLine($"[listener] {msg}");
```

You'll see lines like:

```
[TcpListener] listening on 0.0.0.0:51413
[TcpListener] inbound peer 192.0.2.7:54322 matched torrent abcdef0123456789...
[TcpListener] non-BitTorrent handshake from 198.51.100.4:31900 - dropping
```

## How it works

When a remote peer connects:

1. The accept loop hands the socket to a per-peer handler task (the loop never blocks on slow / malicious clients).
2. The handler **peeks** the first 68 bytes of the BitTorrent handshake using `socket.Receive(SocketFlags.Peek)`. The bytes stay in the kernel buffer.
3. The handshake is validated against the BEP 3 protocol stamp (`0x13` + `"BitTorrent protocol"`). Anything else is dropped (port scan, HTTP probe, etc.).
4. The 20-byte info_hash field is extracted (offset 28..47) and matched against `WebTorrentClient.Torrents` by `WireInfoHashHex` - this routes pure-v2 torrents (which use the first 20 bytes of the SHA-256 v2 info hash on the wire, per libtorrent / qBittorrent / rqbit convention) alongside v1 + hybrid torrents.
5. If matched: a new `TcpPeer` is created in responder mode, the still-unconsumed socket is attached, `Torrent.AddPeer` runs synchronously and wires `OnData` -> `Wire.DataReceived`, and only **then** does the read loop start. The handshake bytes that were peeked are still buffered, so `Wire` reads them fresh from the stream.
6. If unmatched (unknown info_hash) or malformed: the socket is closed.

Why peek instead of buffering? `MSG_PEEK` is non-destructive — the bytes are still available for `Wire.cs` to parse normally. No special "pre-read buffer" plumbing is needed inside `Wire`. The peek timeout is 10 seconds; a real peer sends 68 bytes immediately on connect, so this is generous.

## Production deployments

### Port forwarding

A listener on `IPAddress.Any` will bind every interface, but external peers still need a path through your NAT or firewall. Two options:

- **Static port forward.** Pick a fixed port (e.g. `51413`), set `TcpListenPort = 51413` on the options, then forward `TCP 51413` from your router to the host. Most home routers do this in 30 seconds; the port number is yours to pick.
- **UPnP / NAT-PMP.** SpawnDev.WebTorrent does **not** ship UPnP today (it's tracked on the roadmap). For now, static port forwarding is the supported deployment shape on residential connections.

When the listener is bound to `0.0.0.0` and reachable from outside, you're a full BitTorrent peer - you'll see inbound dials from peers in any swarm whose info_hash matches a torrent in your client.

### Firewall

Windows Firewall will prompt on first bind. Allow it for the network profile you want (private = LAN only; public = internet). On Linux, open the port via `ufw allow 51413/tcp` or equivalent.

### Picking a port

Mainline tooling tradition is `51413` (Transmission default). qBittorrent defaults to a random high port per install. Anything in the 49152-65535 range is typically free. If you're running multiple WebTorrent instances on one host, give each a distinct port (or use `TcpListenPort = 0` and let the kernel pick).

### Disposing

`WebTorrentClient.DisposeAsync` releases the listener cleanly. The accept loop cancels, the listening socket stops, and any in-flight handshake handlers exit on the next iteration. No special teardown is required from your code.

## Mainline-client dial-in pattern

Once your listener is reachable, mainline peers find you via the usual BitTorrent peer-discovery channels. The most direct test is **manual peer addition** through a mainline Web UI:

### qBittorrent (5.0+)

```bash
# Authenticated POST to /api/v2/torrents/addPeers (since qBittorrent 4.4)
curl -X POST -d "hashes=<your-torrent-hash>&peers=<your-ip>:<your-port>" \
     -b cookies.txt http://qbittorrent.host:8080/api/v2/torrents/addPeers
```

This is exactly what `interop_test/qbittorrent_reverse_liveswarm.cs` does on every run. qBittorrent dials the address, our listener accepts, the handshake routes to the matching torrent, and qBittorrent leeches over the resulting wire. 1 MiB hybrid torrent transfers SHA-256 byte-identical end-to-end in a few seconds on localhost.

### Transmission, libtorrent

`transmission-remote --add-peer ip:port` and the libtorrent `add_peer` API both speak the same idea. Any client that supports manual peer addition is a one-liner away from leeching off your listener.

### Tracker-mediated dial-in (`AdvertiseTcpListenerToTrackers`)

The richer dial-in path is **automatic discovery via tracker**: when a mainline peer queries the tracker for peers in your swarm, the tracker returns your IP+port, and the peer dials in without any human intervention. Enable it with one option flip:

```csharp
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    TcpListenPort = 51413,
    AdvertiseTcpListenerToTrackers = true,   // tell trackers our actual port
});
```

When `AdvertiseTcpListenerToTrackers = true` AND a `TcpListener` is bound, every HTTP / UDP tracker announce includes the listener's port in the BEP 3 `port=` field. Trackers put us in their compact peer list and any peer subscribed to that tracker can dial us without manual `addPeers` calls.

| State | HTTP tracker `port=` | UDP tracker port | Effect |
|-------|---------------------|------------------|--------|
| `AdvertiseTcpListenerToTrackers = false` (default) | `0` | `6881` | Legacy 3.1.7 behavior - we don't enter the swarm's dial-in pool. |
| `AdvertiseTcpListenerToTrackers = true`, no listener | `0` | `6881` | No-op - same as default. |
| `AdvertiseTcpListenerToTrackers = true`, listener bound | `<actual port>` | `<actual port>` | Mainline peers find us automatically through the tracker. |

WebSocket trackers (WebRTC signaling) ignore the field - their peer-pairing model is SDP-based, not IP+port-based.

Inspect the runtime decision via `WebTorrentClient.AdvertisedTcpPort` (returns 0 when not advertising; the actual listener port when on).

DHT-based discovery (BEP 5) is on the roadmap; until then, tracker-based discovery is the production path for automatic mainline dial-in.

## Reference

- Source: [`SpawnDev.WebTorrent/TcpListenerService.cs`](../SpawnDev.WebTorrent/TcpListenerService.cs)
- Test: [`PlaywrightMultiTest/DesktopWebRtcTest.cs`](../PlaywrightMultiTest/DesktopWebRtcTest.cs) → `Desktop_TcpListenerOption_AcceptsInboundLeech` (locks the API + verifies a 64 KiB byte-identical transfer over loopback).
- Live external interop: [`interop_test/qbittorrent_reverse_liveswarm.cs`](../interop_test/qbittorrent_reverse_liveswarm.cs) (qBittorrent leeches a 1 MiB hybrid torrent off our listener via WebUI `addPeers`).
- Background: [`Docs/qbittorrent-interop.md`](qbittorrent-interop.md) - what the live-swarm interop matrix proves and how the harness is wired.

## History

- **2026-04-24 night** — `TcpListenerService` shipped in 3.1.7. Surfaced two latent wire-level correctness bugs along the way: (a) `TcpPeer.AttachAsync` started reading before the caller's `OnData` was wired, dropping kernel-buffered handshake bytes (fixed by splitting `AttachAsync` from a new `StartReadLoop`); (b) `Wire._message` was issuing `await _push(header); await _push(data);` as two writes, letting concurrent `SendPiece` responses interleave bytes on the underlying transport (fixed by building each frame in one buffer + a `SemaphoreSlim` around `_push`).
- **2026-04-25 (3.1.8)** — `WebTorrentClient.EnsureTcpListenerAsync` + `WebTorrentClientOptions.TcpListenPort` + `TcpListenAddress` first-class API surface added alongside this doc.
- **2026-04-25 (3.1.8 same day)** — `WebTorrentClientOptions.AdvertiseTcpListenerToTrackers` opt-in flag added: HTTP and UDP tracker announces now include the listener's actual port in the BEP 3 `port=` field, so mainline peers can find us via the tracker without manual `addPeers`. Locked by `Desktop_AdvertiseTcpListenerToTrackers_PutsListenerPortInAnnounce` (stub HTTP tracker captures the announce URL and asserts the port field) + `Desktop_AdvertiseTcpListenerToTrackers_DefaultIsOff` (back-compat default).
