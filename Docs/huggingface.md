# HuggingFace Model Delivery via P2P

Pattern: **a small cache server proxies HuggingFace's CDN; every browser that loads a model becomes a peer for the next.** First request fills the cache from the upstream CDN; everyone after pulls from peers in the swarm + the cache server's HTTP web seed in parallel.

This doc covers **both ends**: how to mount the proxy in your own ASP.NET Core app (server side) and how to consume model files from any proxy in a Blazor WebAssembly app (client side). Either you run the [`SpawnDev.WebTorrent.Server.HuggingFace`](../SpawnDev.WebTorrent.Server.HuggingFace/README.md) extension yourself, or you point at the public `hub.spawndev.com` deployment, or you mix.

## Why P2P here?

AI models are big. Whisper-tiny ONNX is ~30 MB; a Llama 7B model shard is ~13 GB. A vanilla CDN scales fine for the first user but pays linearly per download. SpawnDev.WebTorrent makes the bytes flow peer-to-peer over WebRTC — every user becomes a CDN node for the next. The HuggingFace proxy is the bootstrap: the first download fills the cache from `huggingface.co`, generates a `.torrent` (SHA-256 piece hashes, BEP 52 v2), and announces it on the tracker. The second download has a peer (you) and a fallback web seed (the proxy itself). The third has two peers, etc.

## End-to-end flow

```
   1. First user                                Server
      Browser ─── GET /magnet/{org}/{repo}/{file} ──►   ┐
                                                       │ Cache check
                                                       ▼
                                             ┌─ NOT cached?  ──► fetch from huggingface.co CDN
                                             │   (background async)
                                             │   chunks into 16 KiB Merkle leaves (BEP 52 v2)
                                             │   writes payload + .torrent next to each other
                                             │
                                             └─ Cached?  ──► return magnet immediately
                                                            (xt=urn:btih:<sha1>, ws=/hf/..., xs=/torrent/..., tr=wss://.../announce)

   2. Browser AddAsync(magnet)
      ├─ Connects to tracker (wss://.../announce)
      ├─ Announces in the swarm room (info_hash)
      ├─ Receives peer list (initially empty)
      ├─ Falls back to web seed (HTTP /hf/...) — the proxy IS a peer
      └─ Streams pieces, verifies SHA-256, hands bytes to your code

   3. Second user
      Browser ─── GET /magnet/{org}/{repo}/{file} ──► Server (cached, instant return)
      AddAsync(magnet)
      ├─ Connects to tracker
      ├─ Receives peer list — first user is in there
      ├─ Pulls pieces from peer 1 + web seed in parallel
      └─ Streams bytes

   N. As N grows, the CDN burden falls + delivery speed grows. Public WebTorrent law of cooperative bandwidth.
```

## Server side — running the proxy

You have three deployment shapes:

### Option 1: Use the public `hub.spawndev.com` proxy (zero setup)

Your client just points at the public hub. **No server work required on your part.** Limited to the model catalog actively used through that hub; first-request cache miss falls through to HuggingFace CDN.

```csharp
const string Hub = "https://hub.spawndev.com:44365";
var magnet = await Http.GetStringAsync($"{Hub}/magnet/Xenova/whisper-tiny/onnx/encoder_model.onnx");
```

(See "Client side" below for the full pattern.)

### Option 2: Mount the proxy in your own ASP.NET Core app

```csharp
using SpawnDev.WebTorrent.Server.HuggingFace;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var proxy = new HuggingFaceProxy(new HuggingFaceProxyOptions
{
    CacheDirectory = "hf-cache",                                  // 2 GB headroom default
    TrackerUrls = new[] { "wss://your-tracker.example/announce" }, // your SpawnDev.RTC.Server tracker
    MinFreeDiskSpaceBytes = 2L * 1024 * 1024 * 1024,
});

app.MapHuggingFaceProxy(proxy);
app.Run();
```

Now your app exposes the same five endpoints (`/model`, `/torrent`, `/magnet`, `/hf`, `/hf-stats`) under your domain. Pair with [`SpawnDev.RTC.Server`](../../../SpawnDev.RTC/SpawnDev.RTC.Server/README.md)'s `app.UseRtcSignaling("/announce")` if you want a self-contained tracker too. Full options: [`SpawnDev.WebTorrent.Server.HuggingFace/README.md`](../SpawnDev.WebTorrent.Server.HuggingFace/README.md).

### Option 3: Run `SpawnDev.WebTorrent.ServerApp` standalone

[`SpawnDev.WebTorrent.ServerApp`](https://github.com/LostBeard/SpawnDev.WebTorrent/tree/master/SpawnDev.WebTorrent/SpawnDev.WebTorrent.ServerApp) is the canonical hub binary — single self-contained executable, env-var configurable, runs everything (tracker + STUN/TURN + web seed + HuggingFace proxy + compute request board). This is what `hub.spawndev.com` runs. Bring up your own deployment:

```bash
git clone https://github.com/LostBeard/SpawnDev.WebTorrent
cd SpawnDev.WebTorrent/SpawnDev.WebTorrent
dotnet publish SpawnDev.WebTorrent.ServerApp -c Release -r linux-x64 --self-contained
# Copy bin/Release/net10.0/publish/* to your VM, drop a systemd unit, set env vars.
```

Reference systemd unit lives at [`deploy/spawndev_hub/spawndev_hub.service`](../deploy/spawndev_hub/spawndev_hub.service) — it's the exact unit that runs on hub.spawndev.com. Configurable via env vars for cache dir, tracker URL, max cache size, STUN/TURN, Origin allowlist.

## Client side — Blazor WASM consumer

Two patterns: **one-line magnet** (simplest) or **direct .torrent fetch** (when you want the metadata before streaming).

### Pattern 1: magnet URI (simplest)

The proxy hands you a magnet URI with everything pre-wired (info_hash, tracker, web seed). You pass it to `WebTorrentClient.AddAsync` and stream:

```csharp
@inject WebTorrentClient Client
@inject HttpClient Http

@code {
    const string Proxy = "https://hub.spawndev.com:44365";

    async Task LoadModel()
    {
        // 1. Ask the proxy for a magnet. The proxy returns immediately
        //    if cached, or starts a background fetch if not (returns
        //    status="preparing" — your code can poll /model/... to see).
        var magnetJson = await Http.GetFromJsonAsync<MagnetResult>(
            $"{Proxy}/magnet/Xenova/whisper-tiny/onnx/encoder_model.onnx");
        var magnet = magnetJson!.MagnetUri;

        // 2. Add to the WebTorrent client. Returns once metadata is resolved
        //    (one round-trip to the tracker). Pieces start downloading
        //    immediately — over WebRTC from peers AND HTTP from the proxy's
        //    web seed in parallel.
        var torrent = await Client.AddAsync(magnet);

        // 3. Read the file. Pieces stream in as they arrive; ReadAsync
        //    awaits the requested range.
        var bytes = await torrent.Files[0].ReadAsync(0, (int)torrent.Files[0].Length);

        // 4. Hand to ONNX Runtime / your inference code.
        var session = new InferenceSession(bytes);
    }

    record MagnetResult(string MagnetUri, string RepoId, string FilePath, string WebSeed);
}
```

That's it. Five lines of glue once `WebTorrentClient` is registered in DI (see the project [README](../README.md) "Quick Start — Blazor WebAssembly"). You get:

- Tracker discovery + WebRTC peer pairing automatically
- HTTP web seed fallback automatically (the proxy is always a seed)
- SHA-256 piece verification automatically (BEP 52 v2)
- Resume across page reloads automatically (OPFS persistence — the second visit picks up where the first left off)
- Streaming + range support — for large models, use `torrent.Files[0].CreateReadStream()` instead of `ReadAsync` to read incrementally

### Pattern 2: streaming a model into ONNX Runtime (large files)

For multi-GB models, don't buffer the whole thing into memory:

```csharp
var torrent = await Client.AddAsync(magnet);

using var stream = torrent.Files[0].CreateReadStream();   // .NET Stream
var session = await Task.Run(() => new InferenceSession(stream));
// Pieces download as InferenceSession.LoadModel reads through the stream.
```

`CreateReadStream` returns a `System.IO.Stream` backed by torrent pieces. Seek works. Range reads work. Pieces download on demand to satisfy reads.

### Pattern 3: media element streaming (audio / video models)

If the "model" is a media file (TTS output, sample audio, etc.), use the service worker streaming path — no read-into-memory needed:

```csharp
var torrent = await Client.AddAsync(magnet);
videoElement.Src = torrent.Files[0].StreamURL;
// videoElement is now seeking / buffering directly from torrent pieces via the
// service worker's /webtorrent/{hash}/{fileIdx} interceptor. See Docs/service-worker.md.
```

### Pattern 4: status polling (cache miss UX)

If the file isn't cached yet, `/magnet/...` may return `status="preparing"` instead of a usable magnet. Poll `/model/...` for the JSON status:

```csharp
async Task<string> WaitForReadyMagnet(string repoId, string filePath, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var resp = await Http.GetFromJsonAsync<ModelRequestResult>(
            $"{Proxy}/model/{repoId}/{filePath}");
        if (resp?.Status == "ready" && !string.IsNullOrEmpty(resp.MagnetUri))
            return resp.MagnetUri;
        await Task.Delay(2000);  // poll every 2s while preparing
    }
    throw new TimeoutException($"Model not ready within {timeout}");
}

record ModelRequestResult(string Status, string? MagnetUri, string? TorrentUrl,
                          string? WebSeed, string HuggingFaceUrl);
```

## Live example against `hub.spawndev.com`

You can test all of this right now without writing any code. Run from a shell:

```bash
# 1. Get the magnet
curl -k 'https://hub.spawndev.com:44365/magnet/Xenova/whisper-tiny/onnx/encoder_model.onnx' | jq

# 2. Get the torrent (bencoded, save and feed to qBittorrent / libtorrent / SpawnDev.WebTorrent)
curl -k 'https://hub.spawndev.com:44365/torrent/Xenova/whisper-tiny/onnx/encoder_model.onnx' -o encoder.torrent

# 3. Range-request the first KB from the web seed (proves seekable + cached)
curl -k -H 'Range: bytes=0-1023' 'https://hub.spawndev.com:44365/hf/Xenova/whisper-tiny/onnx/encoder_model.onnx' | xxd | head

# 4. See the proxy's stats
curl -k 'https://hub.spawndev.com:44365/hf-stats' | jq
```

All four work today. The hub is configured with empty Origin allowlist (open signaling) and tracker-gated TURN; you can use it as a free CDN-replacement for HuggingFace models without any setup.

## Endpoint summary

| Endpoint | Returns | When to use |
|----------|---------|-------------|
| `GET /model/{org}/{repo}/{filePath}` | JSON: `status` (ready/preparing) + magnet/torrent/web-seed URLs | Primary entry point. Non-blocking; spawns background cache fill if needed. |
| `GET /magnet/{org}/{repo}/{filePath}` | JSON: just the magnet URI + a few metadata fields | When you only need the magnet (after you know the file is ready) |
| `GET /torrent/{org}/{repo}/{filePath}` | Bencoded `.torrent` bytes | When you want to feed a `.torrent` directly to a non-Blazor client (qBittorrent, libtorrent, etc.) |
| `GET /hf/{org}/{repo}/{filePath}` | Cached file bytes (with HTTP `Range:` support) | The web seed itself. Direct download if you don't want P2P. |
| `GET /hf-stats` | JSON: cache size, file count, torrent count, per-model request counts | Operator dashboard / monitoring |

## Path conventions

The proxy mirrors HuggingFace's URL shape: `org/repo/path/to/file`. Two commonly-used path styles work transparently:

- **Resolved files** (binary models, weights, configs): `Xenova/whisper-tiny/onnx/encoder_model.onnx`. The proxy fetches `https://huggingface.co/{org}/{repo}/resolve/main/{path}` upstream.
- **Raw files** (READMEs, source, plain text): use the `raw/main/` segment if the upstream path needs it: `org/repo/raw/main/README.md`. Path goes through to `huggingface.co/{org}/{repo}/raw/main/{file}`.

When in doubt, `curl 'https://huggingface.co/{path-you-have}'` and confirm the upstream URL works; the proxy uses the same shape.

## Operator notes

- **Disk.** A single 2 GB safetensors shard fills 2 GB of cache. `MinFreeDiskSpaceBytes` (default 2 GB headroom) is the LRU eviction floor; once disk gets near that threshold, the proxy evicts least-recently-used cached models. `MaxCacheSizeBytes` (default unbounded) is the hard ceiling.
- **First-request UX.** First user pays the upstream CDN download time. Status polling (Pattern 4) keeps your UI honest — show "preparing model..." until `status=="ready"`. Subsequent users see `ready` instantly.
- **Tracker.** Generated `.torrent` files bake in `TrackerUrls` from `HuggingFaceProxyOptions`. If you change tracker URLs after files are cached, regenerate the torrents (delete `cache/<file>.torrent` and re-fetch the magnet endpoint) or accept that old torrents announce against old trackers.
- **Origin allowlist.** Browser fetches to `/model`, `/magnet`, `/torrent`, `/hf`, `/hf-stats` are HTTP — they obey CORS, not the WebSocket-tracker Origin allowlist. The proxy adds `AllowAnyOrigin` CORS by default; tighten in your own deployment if you want.

## Cross-references

- **Server library:** [`SpawnDev.WebTorrent.Server.HuggingFace/README.md`](../SpawnDev.WebTorrent.Server.HuggingFace/README.md) — full options, cache lifecycle, dependencies
- **Tracker / signaling server:** [`SpawnDev.RTC.Server`](https://github.com/LostBeard/SpawnDev.RTC) — pair `app.UseRtcSignaling("/announce")` with `app.MapHuggingFaceProxy(proxy)` for a self-contained hub
- **Client primer:** [`README.md`](../README.md) — `WebTorrentClient` registration, magnet handling, file API
- **Service worker streaming:** [`Docs/service-worker.md`](service-worker.md) — `Files[i].StreamURL` for media elements
- **TCP listener** (desktop seeders): [`Docs/tcp-listener.md`](tcp-listener.md) — accept inbound mainline-client connections to the hub's swarm
- **Hub deployment:** [`deploy/spawndev_hub/spawndev_hub.service`](../deploy/spawndev_hub/spawndev_hub.service) — production systemd unit
