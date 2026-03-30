# Service Worker — Media Streaming, COI, and Blazor Loading

SpawnDev.WebTorrent includes `webtorrent-sw.js`, a combined service worker and Blazor loader that enables:

- **Media streaming with seeking** — Video/audio elements get piece data served on demand via range requests
- **Cross-Origin-Isolation** — COOP/COEP headers for SharedArrayBuffer support
- **Blazor loading** — Waits for the service worker to be ready before starting Blazor WebAssembly
- **Health check** — `/webtorrent-sw-check` returns JSON confirming the SW is active

## How It Works

`webtorrent-sw.js` is a dual-mode script:

- **Page context** (loaded via `<script>` in index.html): Registers itself as a service worker, waits for it to be ready, reloads once to apply COI headers, then dynamically loads `_framework/blazor.webassembly.js`.
- **Service worker context**: Intercepts fetch requests to add COI headers, handle `/webtorrent/` streaming requests, and respond to health checks.

## Setup

### 1. index.html

Replace the static Blazor script tag with the WebTorrent loader:

```html
<body>
    <div id="app">Loading...</div>

    <!-- Do NOT include <script src="_framework/blazor.webassembly.js"> -->
    <!-- The service worker loader handles it after COI is confirmed -->
    <script src="webtorrent-sw.js"></script>
</body>
```

### 2. NuGet Package

The `webtorrent-sw.js` file is included in the SpawnDev.WebTorrent NuGet package. It deploys to the app root automatically via `StaticWebAssetBasePath="/"`. No manual file copying needed.

### 3. Program.cs (Blazor WASM)

Register the required services as singletons:

```csharp
using SpawnDev.AsyncFileSystem;
using SpawnDev.AsyncFileSystem.BrowserWASM;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.WebTorrent;

builder.Services.AddBlazorJSRuntime();

// Cross-platform crypto for BEP 46 signing
if (OperatingSystem.IsBrowser())
    builder.Services.AddSingleton<IPortableCrypto, BrowserWASMCrypto>();
else
    builder.Services.AddSingleton<IPortableCrypto, DotNetCrypto>();

// Persistent file system (OPFS in browser)
builder.Services.AddSingleton<IAsyncFS, AsyncFSFileSystemDirectoryHandle>();

// WebTorrent services — IAsyncBackgroundService singletons
// Started automatically before any pages load
builder.Services.AddSingleton<ServiceWorkerStreamHandler>();
builder.Services.AddSingleton<WebTorrentClient>();

await builder.Build().BlazorJSRunAsync();
```

Both `ServiceWorkerStreamHandler` and `WebTorrentClient` implement `IAsyncBackgroundService`. They are started automatically by SpawnDev.BlazorJS before any page renders. No need to await `Ready` — they are guaranteed to be initialized when your pages load.

## Media Streaming

### URL Format

```
/webtorrent/{infoHashHex}/{fileIndex}
```

### File API

```csharp
// Get the streaming URL
var url = file.StreamURL;

// Set on a video element
file.StreamTo(videoElement);

// Get a .NET Stream (seekable, on-demand piece download)
using var stream = file.CreateReadStream();
```

### Range Request Flow

1. Video element requests `GET /webtorrent/{hash}/{idx}` with `Range: bytes=1000000-1065535`
2. Service worker intercepts the request
3. SW posts the request to the `ServiceWorkerStreamHandler` singleton via MessageChannel
4. `WebTorrentClient.HandleStreamRequest` finds the torrent and creates a `TorrentReadStream`
5. `StreamState` reads 64KB chunks from the stream and sends `Uint8Array` data via the port
6. SW wraps chunks in a `ReadableStream` and returns `206 Partial Content`
7. If a piece isn't downloaded yet, `ReadAsync` prioritizes it and waits — the video buffers

### Protocol

Matches the SpawnDev.BlazorJS.WebDesktop `service-worker-fs.js` protocol exactly:

1. SW posts request to client via `MessageChannel` port
2. Client wires up `port.OnMessage += handler`, calls `port.Start()`, then `port.PostMessage(response)`
3. Initial response has `body: "stream_pull"` for streaming
4. SW creates a `ReadableStream` and sends `{ eventType: 'pull', desiredSize: N }` for each chunk
5. Client reads from `TorrentReadStream` and sends `Uint8Array` chunks back
6. Falsy value (empty string) signals stream end

## Health Check

```csharp
// Verify the SW is active and intercepting
var response = await JS.Fetch("/webtorrent-sw-check");
// Returns: {"name":"SpawnDev.WebTorrent","active":true,"scope":"..."}
```

## Cross-Origin-Isolation

The service worker adds these headers to all same-origin responses:

```
Cross-Origin-Embedder-Policy: credentialless
Cross-Origin-Opener-Policy: same-origin
```

This enables `SharedArrayBuffer` which is required for multi-threaded Wasm.

## Torrent Persistence

Torrents persist across page reloads:

- **Pieces**: Stored in OPFS via `AsyncFSChunkStore` at `webtorrent/{infoHashHex}/piece_{N}`
- **Metadata**: `.torrent` bytes saved at `webtorrent/_state/{infoHashHex}.torrent`
- **On startup**: `WebTorrentClient.InitAsync()` restores all saved torrents automatically
- **On remove**: State file deleted. With `destroyStore: true`, pieces deleted too.

## Lifecycle

1. First visit: `webtorrent-sw.js` runs as a page script, registers itself as a SW
2. SW activates, calls `skipWaiting()` + `clients.claim()`
3. Page reloads to pick up COI headers from the SW
4. On reload: `crossOriginIsolated` is `true`, SW is controlling, Blazor loads
5. `ServiceWorkerStreamHandler` starts (IAsyncBackgroundService), listens for SW messages
6. `WebTorrentClient` starts, restores persisted torrents, registers stream handler
7. Subsequent visits: SW is already active, no reload needed, torrents restored from OPFS
