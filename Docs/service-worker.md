# Service Worker — Media Streaming, COI, and Blazor Loading

SpawnDev.WebTorrent includes `webtorrent-sw.js`, a combined service worker and Blazor loader that enables:

- **Media streaming with seeking** — Video/audio elements get piece data served on demand via range requests
- **Cross-Origin-Isolation** — COOP/COEP headers for SharedArrayBuffer support
- **Blazor loading** — Waits for the service worker to be ready before starting Blazor WebAssembly

## How It Works

`webtorrent-sw.js` is a dual-mode script:

- **Page context** (loaded via `<script>` in index.html): Registers itself as a service worker, waits for it to be ready, reloads once to apply COI headers, then dynamically loads `_framework/blazor.webassembly.js`.
- **Service worker context**: Intercepts fetch requests to add COI headers and handle `/webtorrent/` streaming requests.

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

Register the required services:

```csharp
using SpawnDev.AsyncFileSystem;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;

builder.Services.AddBlazorJSRuntime();

// Cross-platform crypto for BEP 46 signing
if (OperatingSystem.IsBrowser())
{
    builder.Services.AddSingleton<IPortableCrypto, BrowserWASMCrypto>();
    builder.Services.AddSingleton<IAsyncFS, AsyncFSFileSystemDirectoryHandle>();
}
else
{
    builder.Services.AddSingleton<IPortableCrypto, DotNetCrypto>();
}
```

## Media Streaming

### URL Format

```
/webtorrent/{infoHashHex}/{fileIndex}
```

- `infoHashHex` — The torrent's info hash as a lowercase hex string
- `fileIndex` — Zero-based index of the file in the torrent's file list

### Example

```csharp
// After downloading a torrent:
var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
var videoUrl = $"/webtorrent/{hash}/0"; // First file

// Use in a <video> element:
<video src="@videoUrl" controls autoplay></video>
// The browser sends range requests → SW intercepts → Blazor reads pieces → responds
```

### Range Request Flow

1. Video element requests `GET /webtorrent/{hash}/{idx}` with `Range: bytes=1000000-1065535`
2. Service worker intercepts the request
3. SW posts the request to the main window via `MessageChannel`
4. Blazor code reads the requested byte range from `TorrentFileStream.ReadAsync()`
5. Blazor responds with the data + `Content-Range` header
6. SW wraps it in a `206 Partial Content` response and returns it to the video element
7. Video plays with full seeking support

### Handling Stream Requests in Blazor

The main window must listen for `webtorrent-stream` messages from the service worker and respond with piece data. See the demo's `Torrents.razor` for the full implementation:

```csharp
// In OnAfterRenderAsync:
JS.Set("_wtStreamHandler", new ActionCallback<MessageEvent>(HandleStreamRequest));
JS.CallVoid("eval", @"
    navigator.serviceWorker.addEventListener('message', function(e) {
        if (e.data && e.data.type === 'webtorrent-stream' && e.ports && e.ports[0]) {
            window._wtStreamHandler(e);
        }
    });
");

// HandleStreamRequest parses the URL, reads from chunk store, responds via port
```

## Cross-Origin-Isolation

The service worker adds these headers to all same-origin responses:

```
Cross-Origin-Embedder-Policy: credentialless
Cross-Origin-Opener-Policy: same-origin
```

This enables `SharedArrayBuffer` which is required for multi-threaded Wasm (used by SpawnDev.ILGPU's Wasm backend).

## Lifecycle

1. First visit: `webtorrent-sw.js` runs as a page script, registers itself as a SW
2. SW activates, calls `skipWaiting()` + `clients.claim()`
3. Page reloads to pick up COI headers from the SW
4. On reload: `crossOriginIsolated` is `true`, Blazor loads immediately
5. Subsequent visits: SW is already active, no reload needed

## Compatibility

- **Requires service worker support** — All modern browsers (Chrome, Firefox, Safari, Edge)
- **Falls back gracefully** — If SW registration fails or COI can't be established after 2 retries, Blazor loads without COI. Streaming still works if the SW is active.
- **GitHub Pages** — Works on static hosts where you can't set server headers
