# SpawnDev.WebTorrent API

Pure C# WebTorrent client. Same API concepts as [JS WebTorrent](https://github.com/webtorrent/webtorrent/blob/master/docs/api.md), adapted for .NET idioms.

---

## `WebTorrentClient`

### Constructor

```csharp
var client = new WebTorrentClient(opts);
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `PeerId` | `string?` | auto-generated | Wire protocol peer ID (hex). Auto-generates Azureus-style `-WW{ver}-` + random. |
| `NodeId` | `byte[]?` | random 20-byte | DHT node id (BEP 5). Persist + restore across runs to keep your DHT identity stable. |
| `MaxConns` | `int` | `55` | Max peer connections per torrent |
| `EnableWebSeeds` | `bool` | `true` | Enable BEP 19 web seed connections |
| `EnableTrackers` | `bool` | `true` | Enable HTTP/UDP/WebSocket tracker discovery |
| `EnableDht` | `bool` | `true` | Enable DHT (BEP 5) on desktop; ignored in browser |
| `EnableLsd` | `bool` | `true` | Enable Local Service Discovery (BEP 14) on desktop; ignored in browser |
| `EnableUtPex` | `bool` | `true` | Enable peer exchange wire extension (BEP 11) |
| `DefaultTrackers` | `string[]?` | `null` | Trackers merged into every torrent (set to empty array to opt out) |
| `IceServers` | `string[]?` | Google/Twilio STUN | ICE servers for WebRTC |
| `Blocklist` | `HashSet<string>?` | `null` | IP addresses to refuse connections from |
| `DownloadLimit` | `long` | `-1` | Download bytes/sec ceiling. `-1` = unlimited, `0` = paused |
| `UploadLimit` | `long` | `-1` | Upload bytes/sec ceiling. Wins over `BandwidthPolicy` when `>= 0`. |
| `BandwidthPolicy` | `BandwidthPolicy` | `Unlimited` | High-level upload policy. `Conservative` = 256 KiB/s, `Metered` = 64 KiB/s, `SeedingDisabled` = 0, `Custom` = paired with explicit `UploadLimit`. See [bandwidth-policy.md](bandwidth-policy.md). |
| `TcpListenPort` | `int?` | `null` | Desktop only. `null` = no listener, `0` = ephemeral, `>0` = bind that port. Lets mainline clients dial in. See [tcp-listener.md](tcp-listener.md). |
| `TcpListenAddress` | `IPAddress?` | `IPAddress.Any` | Local address for `TcpListenPort`. `IPAddress.Loopback` for localhost-only. |
| `AdvertiseTcpListenerToTrackers` | `bool` | `false` | When `true` AND a `TcpListener` is running, HTTP/UDP tracker announces include the listener's port so other peers find us automatically. |
| `PieceHashEngine` | `IPieceHashEngine?` | `SystemCryptoPieceHashEngine` | Pluggable piece-verification hash engine. See [hash-engine.md](hash-engine.md). |
| `HttpClient` | `HttpClient?` | `new HttpClient()` | HTTP client for web seeds and HTTP trackers |
| `AsyncFileSystem` | `IAsyncFS?` | `null` | Persistent storage (OPFS in browser, native FS on desktop) |
| `Crypto` | `IPortableCrypto?` | `null` | Cross-platform Ed25519 for BEP 44/46 (register via `AddPlatformCrypto()` in DI). |
| `StreamHandler` | `ServiceWorkerStreamHandler?` | `null` | Service worker handler for media streaming |

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `PeerId` | `string` | Peer ID as hex string (40 chars) |
| `PeerIdBuffer` | `byte[]` | Peer ID as 20 bytes |
| `Torrents` | `List<Torrent>` | All active torrents |
| `MaxConns` | `int` | Max connections per torrent |
| `EnableWebSeeds` | `bool` | Whether web seeds are enabled |
| `Ready` | `bool` | Whether the client is ready |
| `Destroyed` | `bool` | Whether the client has been destroyed |
| `UploadRateLimiter` | `RateLimiter` | Upload rate limiter. Set `.Rate` to bytes/sec, -1 for unlimited, 0 to pause. |
| `DownloadRateLimiter` | `RateLimiter` | Download rate limiter. Same. |
| `BandwidthPolicy` | `BandwidthPolicy` | Active upload policy. Use `ApplyBandwidthPolicy(policy)` to flip + re-rate at runtime. |
| `PieceHashEngine` | `IPieceHashEngine` | Piece-verification engine. Default `SystemCryptoPieceHashEngine`; replace for GPU / batched implementations. |
| `Dht` | `DhtDiscovery?` | DHT instance (desktop only, null in browser) |
| `TcpListener` | `TcpListenerService?` | Inbound TCP peer-wire listener (desktop only). Inspect `.LocalEndPoint.Port` for the kernel-assigned port when `TcpListenPort = 0`. |
| `AdvertiseTcpListenerToTrackers` | `bool` | Mirror of the option; flip at runtime to start/stop advertising the listener port to trackers. |
| `AdvertisedTcpPort` | `int` | Read-only: returns the port currently advertised to trackers (0 if disabled or no listener). |
| `StreamHandler` | `ServiceWorkerStreamHandler?` | Service worker stream handler |
| `IceServers` | `string[]` | ICE servers for WebRTC peer connections |
| `AsyncFileSystem` | `IAsyncFS?` | Persistent storage backend |
| `Crypto` | `IPortableCrypto?` | Cross-platform Ed25519 implementation (BEP 44/46) |
| `VerboseLogging` | `static bool` | Enable verbose console output (gate all logging behind this) |

### Methods

#### `Add(string magnetOrInfoHash, AddTorrentOptions? opts = null)`

Start downloading a torrent. Accepts magnet URI or info hash hex string. Returns `Torrent` immediately — metadata resolves asynchronously via ut_metadata (BEP 9).

```csharp
var torrent = client.Add("magnet:?xt=urn:btih:...");
```

#### `Add(byte[] torrentBytes, AddTorrentOptions? opts = null)`

Add a torrent from a `.torrent` file. Metadata is available immediately.

```csharp
var torrent = client.Add(torrentFileBytes);
```

#### `SeedAsync(string name, byte[] data, TorrentCreatorOptions? opts = null, AddTorrentOptions? addOpts = null)`

Start seeding data. Creates a `.torrent`, stores all pieces, and begins serving to peers.

```csharp
var torrent = await client.SeedAsync("model.onnx", modelBytes);
```

#### `SeedAsync(string name, (string path, byte[] data)[] files, ...)`

Seed multiple files as a single torrent.

```csharp
var torrent = await client.SeedAsync("dataset", new[]
{
    ("train.bin", trainData),
    ("labels.bin", labelData),
});
```

#### `RemoveAsync(Torrent torrent)` / `RemoveAsync(string infoHash)`

Remove a torrent. Disposes all connections and resources.

#### `RemoveWithDataAsync(Torrent torrent)` / `RemoveWithDataAsync(string infoHash)`

Remove a torrent and delete all stored data (pieces + metadata) from persistent storage.

#### `Get(string infoHash)`

Returns the `Torrent` with the given info hash, or `null`.

```csharp
var torrent = client.Get("08ada5a7a6183aae1e09d831df6748d566095a10");
```

#### `AddBtpkAsync(byte[] publicKey, byte[]? salt = null, IDhtSigner? signer = null, ...)`

Add a BEP 46 mutable torrent by publisher's Ed25519 public key. Resolves the current info hash from the DHT, downloads it, and subscribes for updates.

```csharp
var torrent = await client.AddBtpkAsync(publisherPublicKey);
```

#### `UseExtension(Func<Wire, IWireExtension> factory)`

Register a wire extension factory. Extensions are created per-peer before the BEP 10 handshake. Must be called before `Add`/`SeedAsync`.

```csharp
client.UseExtension((wire) =>
{
    var ext = new MyExtension();
    ext.SetWire(wire);
    return ext;
});
```

#### `RegisterStreamHandler(ServiceWorkerStreamHandler handler)`

Wire up the service worker stream handler so `file.StreamURL` works.

#### `RestoreFromStorageAsync()`

Restore previously persisted torrents from OPFS/filesystem storage.

#### `EnsureTcpListenerAsync(int port = 0, IPAddress? address = null)`

Desktop only. Bind a `TcpListenerService` and start accepting inbound BitTorrent peer-wire connections from mainline clients (qBittorrent, libtorrent, Transmission, rqbit). Idempotent. `port = 0` lets the kernel assign an ephemeral port (read it back via `client.TcpListener.LocalEndPoint.Port`). Browser-side: no-op. See [tcp-listener.md](tcp-listener.md).

#### `EnsureDhtAsync(DhtOptions? opts = null, CancellationToken ct = default)`

Desktop only. Lazily start the DHT (BEP 5) on a specific port + node id. Calling once with options gives you control over the DHT identity; the lazy paths inside `AddBtpkAsync` etc. start the DHT on the default port if it isn't already running.

#### `ApplyBandwidthPolicy(BandwidthPolicy policy)`

Apply `policy` to `UploadRateLimiter.Rate` and update `BandwidthPolicy` in lockstep so the two can't drift. Useful for "pause seeding while on cellular" / "go full speed at home" UX. See [bandwidth-policy.md](bandwidth-policy.md).

#### `ThrottleUpload(long rate)`

Set `UploadRateLimiter.Rate` directly without touching `BandwidthPolicy`. Use when `BandwidthPolicy = Custom` and you want to pin the exact bytes/sec.

#### `DisposeAsync()`

Destroy the client, all torrents, and any TCP listener.

### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnAdd` | `Action<Torrent>` | Torrent added to client |
| `OnRemove` | `Action<Torrent>` | Torrent removed from client |
| `OnTorrentReady` | `Action<Torrent>` | Torrent metadata resolved and ready |
| `OnDownload` | `Action<int>` | Bytes downloaded across any torrent (bubbles from Wire -> Torrent -> Client) |
| `OnUpload` | `Action<int>` | Bytes uploaded across any torrent (bubbles from Wire -> Torrent -> Client) |
| `OnWarning` | `Action<string>` | Non-fatal warning |
| `OnError` | `Action<Exception>` | Fatal error |
| `OnMutableUpdate` | `Action<Torrent, string>` | BEP 46: mutable torrent updated to new info hash |

### AddTorrentOptions

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `DisableWebSeeds` | `bool` | `false` | Disable web seeds for this torrent |
| `Strategy` | `string` | `"rarest"` | Piece selection strategy: `"rarest"` or `"sequential"` |
| `Path` | `string?` | `null` | Download path (desktop) |
| `Paused` | `bool` | `false` | Add in paused state - metadata downloads but no pieces. Chokes all wires and cancels requests. Auto-resumes on `ReadFileAsync`. |
| `Deselect` | `bool` | `false` | Add without selecting any pieces. Peers connect normally but no pieces are requested until files are individually `Select()`'d. Unlike `Paused`, wires are not choked. |
| `MaxWebConns` | `int` | `4` | Max simultaneous web seed connections for this torrent |
| `NoPeersIntervalTime` | `int` | `30` | Seconds between `OnNoPeers` event checks. Fires per enabled source (tracker, dht) when no peers are connected. |

### TorrentCreatorOptions

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `HashAlgorithm` | `string` | `"SHA-256"` | Piece hash algorithm: `"SHA-256"` or `"SHA-1"` |
| `Trackers` | `string[]?` | `null` | Tracker announce URLs |
| `WebSeeds` | `string[]?` | `null` | Web seed URLs (added to `url-list`) |
| `IsPrivate` | `bool` | `false` | Private torrent flag (BEP 27) |
| `Name` | `string?` | `null` | Override torrent name |
| `Comment` | `string?` | `null` | Author comment |
| `CreatedBy` | `string?` | `null` | Creator string |

---

## `Torrent`

Represents a torrent in the client. Returned by `client.Add()` and `client.SeedAsync()`.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `InfoHash` | `string?` | Info hash as lowercase hex (40 chars) |
| `InfoHashHex` | `string` | Same as `InfoHash` (never null, empty string if unknown) |
| `Name` | `string?` | Torrent name from metadata |
| `PieceLength` | `int` | Bytes per piece (except last) |
| `LastPieceLength` | `int` | Bytes in last piece |
| `Length` | `long` | Total size in bytes (sum of all files) |
| `Paused` | `bool` | Whether torrent is paused |
| `Destroyed` | `bool` | Whether torrent has been destroyed |
| `Done` | `bool` | Whether all pieces are downloaded |
| `Ready` | `bool` | Whether metadata is available and store is ready |
| `HasMetadata` | `bool` | Whether metadata has been resolved |
| `Progress` | `double` | Download progress, 0.0 to 1.0 |
| `Downloaded` | `long` | Verified bytes downloaded |
| `Uploaded` | `long` | Total bytes uploaded to peers |
| `Received` | `long` | Total bytes received (including invalid) |
| `DownloadSpeed` | `double` | Current download speed (bytes/sec) |
| `UploadSpeed` | `double` | Current upload speed (bytes/sec) |
| `Ratio` | `double` | Seed ratio (uploaded / downloaded) |
| `NumPeers` | `int` | Number of connected peers (wire connections) |
| `PeerCount` | `int` | Same as NumPeers |
| `WebSeedCount` | `int` | Number of active web seed connections |
| `PieceCount` | `int` | Total number of pieces |
| `CompletedPieces` | `int` | Number of verified pieces |
| `ComputedMagnetUri` | `string` | Magnet URI with trackers and web seeds |
| `MagnetUri` | `string?` | Original magnet URI (if added via magnet) |
| `TorrentFileBytes` | `byte[]?` | Raw `.torrent` file bytes |
| `TorrentFileBlob` | `Blob?` | Zero-copy JS Blob of the `.torrent` file (browser only). Caller owns the Blob and must dispose it. |
| `Files` | `TorrentFileInfo[]?` | Array of files in the torrent |
| `Pieces` | `Piece[]` | Array of piece objects |
| `Bitfield` | `bool[]` | Per-piece download status |
| `Wires` | `List<Wire>` | Connected peer wire connections |
| `AnnounceUrls` | `string[]` | Tracker announce URLs |
| `Announce` | `string[]` | Alias for AnnounceUrls |
| `UrlList` | `string[]` | Web seed URLs |
| `IsPrivate` | `bool` | Private torrent flag (BEP 27) |
| `Strategy` | `string` | Piece selection strategy: `"rarest"` or `"sequential"` |
| `Comment` | `string?` | Author comment from metadata |
| `CreatedBy` | `string?` | Creator string from metadata |
| `CreationDate` | `DateTimeOffset?` | Torrent creation timestamp |
| `SelectedFileIndices` | `int[]?` | BEP 53: selected file indices from `so=` magnet param |
| `DownloadSpeedHistory` | `List<double>` | 60-second speed history for sparklines |
| `UploadSpeedHistory` | `List<double>` | 60-second speed history for sparklines |
| `BtpkPublicKey` | `byte[]?` | BEP 46: publisher's public key (if mutable torrent) |
| `IsMutableTorrent` | `bool` | Whether this is a BEP 46 mutable torrent |

### Methods

#### `Pause()`

Stop downloading. Chokes all wires and cancels outstanding requests. Metadata exchange continues (ut_metadata still works for magnet resolution).

#### `Resume()`

Resume downloading. Re-expresses interest and requests pieces.

#### `Select(int startPiece, int endPiece, int priority = 1)`

Prioritize a range of pieces for download.

#### `Deselect(int startPiece, int endPiece)`

Remove priority for a piece range.

#### `Critical(int startPiece, int endPiece)`

Mark pieces as critical (highest priority). Used internally by `ReadFileAsync` to prioritize pieces needed for streaming.

#### `AddPeer(SimplePeer peer)`

Add a peer to the swarm.

#### `AddWebSeed(string url)`

Add a web seed URL to this torrent.

#### `ReadFileAsync(int fileIndex, CancellationToken ct = default)`

Read an entire file. Waits for all pieces to download.

```csharp
var data = await torrent.ReadFileAsync(0);
```

#### `ReadFileAsync(int fileIndex, long offset, int length, CancellationToken ct = default)`

Read a byte range from a file. Waits for needed pieces on demand. Auto-selects and auto-resumes if torrent was paused.

```csharp
var chunk = await torrent.ReadFileAsync(0, offset: 1_000_000, length: 65536);
```

#### `DisposeAsync()`

Destroy the torrent, close all connections, stop timers.

#### `NotifyMutableUpdate(string newInfoHash)`

Fire the `OnMutableUpdate` event (used by BEP 46 subscription system).

### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnWire` | `Action<Wire, string>` | New peer wire connected (wire, peerId) |
| `OnMetadata` | `Action` | Metadata resolved (files, pieces available) |
| `OnReady` | `Action` | Torrent ready for use |
| `OnInfoHash` | `Action` | Info hash determined (fires after magnet parse or metadata set, before `OnReady`) |
| `OnDownload` | `Action<int>` | Bytes downloaded from a peer (bubbles from Wire -> Peer -> Torrent) |
| `OnUpload` | `Action<int>` | Bytes uploaded to a peer (bubbles from Wire -> Peer -> Torrent) |
| `OnDone` | `Action` | All pieces downloaded and verified |
| `OnIdle` | `Action` | All selections complete, now seeding. Fires alongside `OnDone`. |
| `OnNoPeers` | `Action<string>` | No peers found via source. Arg is source name: `"tracker"`, `"dht"`. Fires on a periodic timer. |
| `OnWarning` | `Action<string>` | Non-fatal warning |
| `OnPieceVerified` | `Action<int>` | Piece verified successfully (piece index) |
| `OnMutableUpdate` | `Action<string>` | BEP 46: new info hash published by mutable torrent owner |

---

## `TorrentFileInfo`

Represents a file within a torrent. Accessed via `torrent.Files[i]`.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Filename |
| `Path` | `string` | File path within torrent |
| `Length` | `long` | File size in bytes |
| `Offset` | `long` | Byte offset within the torrent's concatenated data |
| `Type` | `string` | MIME type (derived from file extension) |
| `Downloaded` | `long` | Verified bytes downloaded for this file |
| `Progress` | `double` | Download progress, 0.0 to 1.0 |
| `Done` | `bool` | Whether this file is fully downloaded |
| `StartPiece` | `int` | First piece index containing this file's data |
| `EndPiece` | `int` | Last piece index containing this file's data |
| `StreamURL` | `string?` | Service worker streaming URL. Point `<video src="@StreamURL">` at this for streaming with seeking while downloading. |

### Methods

#### `Select()`

Select this file for download. Marks the file's piece range for download.

#### `Deselect()`

Deselect this file. Pieces for this file won't be requested.

#### `ReadAsync(long offset, int length, CancellationToken ct = default)`

Read a byte range from this file. Waits for needed pieces on demand.

```csharp
var chunk = await file.ReadAsync(0, 4096);
```

#### `GetArrayBufferAsync(CancellationToken ct = default)`

Get the entire file as a byte array. Blocks until all pieces are downloaded.

```csharp
var data = await file.GetArrayBufferAsync();
```

#### `CreateReadStream(long start = 0)`

Get a seekable `System.IO.Stream` backed by torrent pieces. Pieces download on demand as the stream is read. Works on both desktop and browser (async reads only in browser).

```csharp
using var stream = file.CreateReadStream();
var buffer = new byte[4096];
var bytesRead = await stream.ReadAsync(buffer);
stream.Position = 1_000_000; // seek
bytesRead = await stream.ReadAsync(buffer);
```

#### `BlobAsync(CancellationToken ct = default)`

Get the entire file as a JS `Blob` for zero-copy download links or object URLs. Browser only. Waits for all pieces to download.

```csharp
var blob = await file.BlobAsync();
var url = URL.CreateObjectURL(blob);
```

#### `StreamTo(HTMLMediaElement elem)`

Set an HTML media element's `src` to this file's streaming URL. Browser only. Supports streaming with seeking while the torrent downloads.

```csharp
file.StreamTo(videoElement);
```

#### `Includes(int pieceIndex)`

Returns `true` if the given piece index contains data from this file.

### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnDone` | `Action` | File fully downloaded |

---

## `Wire`

Represents a peer connection with the BitTorrent wire protocol.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `PeerId` | `string?` | Remote peer ID (hex) |
| `PeerIdBuffer` | `byte[]?` | Remote peer ID (20 bytes) |
| `Type` | `string?` | Connection type: `"webrtc"`, `"tcpOutgoing"`, `"tcpIncoming"` (from `TcpListenerService`), `"webSeed"` |
| `Destroyed` | `bool` | Whether the wire is destroyed |
| `AmChoking` | `bool` | Whether we are choking this peer |
| `AmInterested` | `bool` | Whether we are interested in this peer |
| `PeerChoking` | `bool` | Whether the peer is choking us |
| `PeerInterested` | `bool` | Whether the peer is interested in us |
| `Requests` | `List<WireRequest>` | Outstanding outgoing requests |
| `PeerRequests` | `List<WireRequest>` | Outstanding incoming requests |
| `PeerExtendedHandshake` | `Dictionary<string, object>?` | BEP 10 extended handshake data from peer |
| `Extensions` | `WireExtensions` | Our extension flags (Extended, Dht, Fast) |
| `PeerExtensions` | `WireExtensions?` | Peer's extension flags |
| `HasFast` | `bool` | Whether BEP 6 Fast Extension is active |
| `PeerHasAll` | `bool` | Whether peer has all pieces (HaveAll) |
| `PeerPieces` | `bool[]?` | Per-piece availability from peer |
| `ExtendedHandshake` | `Dictionary<string, object>` | Our extended handshake data to send |

### Methods

#### `Destroy()`

Close this peer connection.

#### `Choke()` / `Unchoke()` / `Interested()` / `Uninterested()`

Send choke/unchoke/interested/uninterested messages.

#### `Have(int pieceIndex)`

Announce that we have a piece.

#### `Bitfield(bool[] bitfield)`

Send our bitfield to the peer.

#### `Request(int index, int offset, int length, Action<Exception?, byte[]?> callback)`

Request a block from the peer.

#### `Cancel(int index, int offset, int length)`

Cancel a pending request.

#### `Extended(string name, byte[] data)`

Send a BEP 10 extended message.

#### `Use(IWireExtension extension)`

Register a wire extension. Must be called before handshake.

#### `GetExtension(string name)` / `GetExtension<T>()`

Look up a registered extension by name or type.

```csharp
var pex = wire.GetExtension<UtPexExtension>();
var ext = wire.GetExtension("sd_compute");
```

#### `DataReceived(byte[] data)`

Feed raw bytes from the transport into the wire protocol parser.

### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnHandshake` | `Action<string, string, WireExtensions>` | Handshake received (infoHash, peerId, extensions) |
| `OnChoke` / `OnUnchoke` | `Action` | Choke state changed |
| `OnInterested` / `OnUninterested` | `Action` | Interest state changed |
| `OnHave` | `Action<int>` | Peer has piece (index) |
| `OnBitfield` | `Action<byte[]>` | Peer bitfield received |
| `OnRequest` | `Action<int, int, int, Action<Exception?, byte[]?>>` | Peer requests block (index, offset, length, respond) |
| `OnPiece` | `Action<int, int, byte[]>` | Block received (index, offset, data) |
| `OnCancel` | `Action<int, int, int>` | Request cancelled (index, offset, length) |
| `OnPort` | `Action<int>` | DHT port received |
| `OnHaveAll` / `OnHaveNone` | `Action` | BEP 6 Fast Extension |
| `OnDownload` | `Action<int>` | Bytes downloaded from peer (piece data received) |
| `OnUpload` | `Action<int>` | Bytes uploaded to peer (piece data sent) |
| `OnExtended` | `Action<string, byte[]>` | BEP 10 extended message (name, data) |
| `OnClose` | `Action` | Wire closed |

---

## `RateLimiter`

Token bucket rate limiter for upload/download throttling.

```csharp
client.UploadRateLimiter.Rate = 50_000;   // 50 KB/sec
client.DownloadRateLimiter.Rate = -1;     // unlimited
client.UploadRateLimiter.Rate = 0;        // paused
```

| Property | Type | Description |
|----------|------|-------------|
| `Rate` | `long` | Bytes per second. `-1` = unlimited, `0` = paused, positive = throttled. |

### Methods

#### `WaitAsync(int bytes, CancellationToken ct = default)`

Wait until the specified number of bytes can be transferred. Returns immediately if unlimited.

---

## `TorrentCreator`

Static methods for creating `.torrent` files.

#### `CreateFromBytes(string name, byte[] data, TorrentCreatorOptions? opts = null)`

Create a torrent from in-memory bytes. Returns `(byte[] torrentBytes, TorrentMetadata metadata)`.

#### `CreateFromMultipleFiles(string name, (string path, byte[] data)[] files, TorrentCreatorOptions? opts = null)`

Create a multi-file torrent. Returns `(byte[] torrentBytes, TorrentMetadata metadata)`.

#### `CreateFromStreamAsync(string name, Stream stream, long length, TorrentCreatorOptions? opts = null)`

Create a torrent from any `System.IO.Stream`.

#### `CreateFromUrlAsync(string name, string url, TorrentCreatorOptions? opts = null)`

Create a torrent by streaming from a URL. The URL is automatically added as a web seed.

#### `CreateFromFileAsync(string filePath, TorrentCreatorOptions? opts = null)`

Create a torrent from a local file (desktop only).

---

## `ServiceWorkerStreamHandler`

Singleton service that routes streaming requests from the service worker to torrents. Register in DI and pass to `WebTorrentClient` via options.

```csharp
builder.Services.AddSingleton<ServiceWorkerStreamHandler>();
// ... then in client factory:
var client = new WebTorrentClient(new WebTorrentClientOptions { StreamHandler = handler });
```

### Static Methods

#### `GetStreamUrl(Torrent torrent, int fileIndex)` / `GetStreamUrl(Torrent torrent, TorrentFileInfo file)`

Get the service worker streaming URL for a torrent file.

---

## BEP 46: `AgentChannel`

High-level pub/sub for AI agent communication over DHT mutable items.

```csharp
// Desktop (DHT transport)
var channel = new AgentChannel(dht, signer);

// Publish state
await channel.PublishStateAsync(stateBytes, "model-weights");

// Subscribe to another agent
channel.OnAgentUpdate += (pubKey, value, seq) => { /* new state */ };
await channel.SubscribeAsync(otherAgentPublicKey, "model-weights");

// Named sub-channels
var weights = channel.Channel("model-weights");
var health = channel.Channel("health");
```

---

## Platform Support

| Feature | Desktop (.NET) | Browser (Blazor WASM) |
|---------|---------------|----------------------|
| WebRTC peers | RtcPeer (SpawnDev.RTC bridges to a SipSorcery fork) | RtcPeer (SpawnDev.RTC bridges to BlazorJS `RTCPeerConnection`) |
| TCP peers (outbound) | TcpPeer.ConnectAsync | N/A (no sockets) |
| TCP listener (inbound) | TcpListenerService — accepts mainline-client dials | N/A (no listening sockets in browser) |
| DHT (BEP 5) | DhtDiscovery | N/A (no UDP) |
| UDP tracker (BEP 15) | UdpTrackerClient | N/A (no UDP) |
| Local discovery (BEP 14) | LocalServiceDiscovery | N/A (no UDP multicast) |
| WebSocket tracker | WebSocketTracker | WebSocketTracker |
| HTTP tracker | HttpTracker | HttpTracker |
| Web seeds | WebConn | WebConn |
| OPFS storage | N/A | AsyncFSChunkStore |
| File storage | FileChunkStore | N/A |
| Memory storage | MemoryChunkStore | MemoryChunkStore |
| Media streaming | TorrentHttpServer | ServiceWorkerStreamHandler |
| Ed25519 signing | System.Security.Cryptography | WebCrypto (SubtleCrypto) |
