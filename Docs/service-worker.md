# Service Worker — `webtorrent-sw.js`

`webtorrent-sw.js` is a single combined script that ships with the SpawnDev.WebTorrent NuGet package. It plays two roles depending on which context it runs in:

- **In the page (window) context:** registers itself as a service worker, waits for Cross-Origin-Isolation, and loads `blazor.webassembly.js`.
- **In the service-worker (self) context:** intercepts `/webtorrent/{infoHash}/{fileIdx}` requests, adds COOP/COEP headers (for `SharedArrayBuffer`), and serves torrent file ranges back via `ReadableStream`.

Same file, two roles. The runtime detects which context loaded it.

## Why this exists

Two unrelated browser-platform requirements collide on one library:

1. **Cross-Origin Isolation** (`SharedArrayBuffer` / WebGPU prerequisites). Requires the page to be served with `Cross-Origin-Opener-Policy: same-origin` + `Cross-Origin-Embedder-Policy: require-corp`. Most static hosts (GitHub Pages included) won't add those headers. A service worker can — by intercepting every fetch and stamping the headers in itself.
2. **Streaming a torrent into a `<video>` / `<audio>` element with seeking.** Browser media elements demand HTTP `Range:` request support. The torrent client lives in the page context with the pieces in OPFS — the service worker intercepts `/webtorrent/{hash}/{fileIdx}` requests, sends the byte range over a `MessageChannel` to the page, and streams back a synthetic `206 Partial Content` response.

Same file does both because the registration cost is identical and Blazor needs to know the SW is active before it can use `SharedArrayBuffer`-dependent features.

## Wiring it in

In your Blazor WASM app's `wwwroot/index.html`, replace the default Blazor script tag:

```html
<!-- Replace this: -->
<script src="_framework/blazor.webassembly.js"></script>

<!-- With this: -->
<script src="webtorrent-sw.js"></script>
```

The script picks up the original Blazor loader in its page-context branch. No other html / `Program.cs` changes required.

The file deploys to your app root automatically — `SpawnDev.WebTorrent.csproj` declares `StaticWebAssetBasePath="/"`, which puts the wwwroot bits at `/` instead of `/_content/SpawnDev.WebTorrent/`. SW scope is the entire app.

## DI registration

Register the stream handler alongside the client. Both are `IAsyncBackgroundService`:

```csharp
builder.Services.AddSingleton<ServiceWorkerStreamHandler>();
builder.Services.AddSingleton<WebTorrentClient>();
```

The client picks up the handler automatically (constructor DI). When you `Add` a torrent, the handler's `OnRequest` is wired so subsequent SW range requests find the right pieces.

## What the SW does (page context)

1. Detects `serviceWorker in navigator`. If absent, loads Blazor directly — no COI or streaming, but the app still works for non-streaming uses.
2. Registers itself (`navigator.serviceWorker.register`).
3. Checks `window.crossOriginIsolated`:
   - **Already isolated + SW controlling:** load Blazor immediately.
   - **Isolated but SW not yet controlling:** wait for `serviceWorker.ready`, reload once, load Blazor on the post-reload page (the reload makes the SW intercept Blazor's framework fetches for COEP).
   - **Not isolated yet:** wait for the SW to activate, reload to pick up the COOP/COEP headers it adds.
4. The reload counter (`sessionStorage["coi-sw-reload"]`) prevents infinite reload loops if something is broken upstream — caps at 1 retry.

Verbose logging is gated behind a top-of-file `verbose` flag — flip to `true` for `[COI] ...` traces during integration.

## What the SW does (worker context)

The SW listens on `fetch` and handles three kinds of requests:

### 1. `/webtorrent-sw-check` — health endpoint

```js
fetch('/webtorrent-sw-check').then(r => r.json())
// → { active: true, version: '...', timestamp: 1234567890 }
```

Useful for the page to confirm "yes, the SW is wired, streaming will work" before calling `Torrent.Files[0].StreamURL`. Returns 503 with `{ active: false }` if the SW is somehow not in control.

### 2. `/webtorrent/{infoHash}/{fileIdx}[?...]` — torrent stream interceptor

The SW does **not** hold the pieces — those live in OPFS in the page context. Instead it runs a request/response protocol over `MessageChannel`:

1. SW receives the fetch event, parses `(infoHash, fileIdx, Range)` from the URL + headers.
2. SW iterates `clients.matchAll()`, picks the first window client.
3. SW posts `{ type: 'webtorrent-request', infoHash, fileIdx, range }` to the client via the channel's `port2`.
4. The page-side `ServiceWorkerStreamHandler` receives the message, looks up the torrent by `WireInfoHashHex`, opens a `TorrentReadStream` over the file at the requested range, and posts chunks back through `port1`.
5. SW assembles chunks into a `ReadableStream`, builds a `Response` with `206 Partial Content` + `Content-Range`, and resolves the original fetch with it.

The streaming is **lazy** — pieces download as the consumer reads. This is what makes `<video src="...">` seeking work without the whole file being downloaded first.

### 3. Everything else — pass-through with COOP/COEP headers stamped

The SW intercepts every other fetch, lets it go to the network, then clones the response and adds:

```
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
Cross-Origin-Resource-Policy: same-origin
```

This is what makes `SharedArrayBuffer` available to your Blazor app even on static hosts that don't set these headers themselves.

## File API integration

Once the SW is active, every `Torrent.Files[i]` exposes:

| Member | Returns | Description |
|--------|---------|-------------|
| `file.StreamURL` | `string` | `/webtorrent/{infoHash}/{fileIdx}` — drop into `<video src=...>` / `<audio src=...>` directly. |
| `file.StreamTo(elem)` | `Task` | Sets `elem.SrcObject` / `elem.Src` for a typed `HTMLMediaElement`. |
| `file.CreateReadStream(start, end)` | `Stream` | .NET `Stream` over the file range. |
| `file.ReadAsync(offset, length)` | `Task<byte[]>` | One-shot range read. |

`StreamURL` is the most-used path — it's what makes a torrent-backed `<video>` "just work" in the browser.

## Versioning + auto-update

The SW file's bytes are the cache key. Any byte change → browser fetches the new version on next page load → the new SW activates after old clients close. SpawnDev.WebTorrent's NuGet ships the same SW bytes for the lifetime of a major+minor version, so consumer apps don't pick up surprise SW updates mid-session.

If you're adapting `webtorrent-sw.js` for your own app, change the version comment at the top — that triggers byte-level diff and guarantees the new SW takes over.

## Diagnostics

| Symptom | Likely cause | Check |
|---------|--------------|-------|
| `<video>` shows broken icon, network tab shows 404 on `/webtorrent/.../0` | SW not active or not controlling | `fetch('/webtorrent-sw-check')` returns `{ active: false }` |
| Blazor loads but `SharedArrayBuffer` is undefined | COOP/COEP not landed | Open DevTools → Application → Service Workers; verify the SW is **activated** AND **controlling**. Hard reload (Ctrl+Shift+R) once. |
| Reload loop on first visit | `coi-sw-reload` counter at 1+ | Script auto-caps at 1 retry; second time it falls through to direct Blazor load. Check for SW registration errors in console. |
| Range requests work but seeking is slow | Pieces aren't downloaded yet | Expected — SpawnDev.WebTorrent prioritizes the requested range, but pieces still arrive at swarm speed. |

## Reference

- Source: [`SpawnDev.WebTorrent/wwwroot/webtorrent-sw.js`](../SpawnDev.WebTorrent/wwwroot/webtorrent-sw.js)
- Page-side handler: [`SpawnDev.WebTorrent/ServiceWorkerStreamHandler.cs`](../SpawnDev.WebTorrent/ServiceWorkerStreamHandler.cs)
- Stream URL builder: `Torrent.File.StreamURL` in [`SpawnDev.WebTorrent/Torrent.cs`](../SpawnDev.WebTorrent/Torrent.cs)
- The `webtorrent-sw.js` file's contract is intentionally identical to `service-worker-fs.js` in [SpawnDev.BlazorJS.WebDesktop](https://github.com/LostBeard/SpawnDev.BlazorJS.WebDesktop) — same `MessageChannel` protocol, different stream provider on the page side.
