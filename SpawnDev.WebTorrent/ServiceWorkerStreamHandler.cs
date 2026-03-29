using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Handles service worker streaming requests for torrent data.
/// Listens for 'webtorrent' messages from the service worker and responds
/// with piece data via MessageChannel for video/audio streaming with seeking.
///
/// Call EnableServiceWorkerStreaming() on your WebTorrentClient to activate.
/// </summary>
public class ServiceWorkerStreamHandler : IDisposable
{
    private readonly WebTorrentClient _client;
    private readonly Action<MessageEvent> _handler;
    private bool _disposed;

    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mkv"] = "video/x-matroska",
        [".ogv"] = "video/ogg", [".mov"] = "video/quicktime",
        [".mp3"] = "audio/mpeg", [".ogg"] = "audio/ogg", [".flac"] = "audio/flac",
        [".wav"] = "audio/wav", [".aac"] = "audio/aac", [".opus"] = "audio/opus",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    public ServiceWorkerStreamHandler(WebTorrentClient client)
    {
        _client = client;
        _handler = HandleMessage;

        if (!OperatingSystem.IsBrowser()) return;

        var swContainer = BlazorJSRuntime.JS.Get<ServiceWorkerContainer>("navigator.serviceWorker");
        if (swContainer == null) return;
        swContainer.OnMessage += _handler;
    }

    private void HandleMessage(MessageEvent msgEvent)
    {
        try
        {
            using var data = msgEvent.GetData<JSObject>();
            var msgType = data.JSRef!.Get<string?>("type");
            if (msgType != "webtorrent") return;

            var requestUrl = data.JSRef!.Get<string>("url") ?? "";
            using var headersObj = data.JSRef!.Get<JSObject?>("headers");
            var rangeHeader = headersObj?.JSRef?.Get<string?>("range");
            var destination = data.JSRef!.Get<string?>("destination") ?? "";

            var ports = msgEvent.Ports;
            if (ports == null || ports.Length == 0) { ports?.Dispose(); return; }
            var port = ports[0];
            ports.Dispose();

            // Parse URL: .../webtorrent/{infoHash}/{fileIndex}
            if (!TryParseStreamUrl(requestUrl, out var infoHash, out var fileIdx))
            {
                port.PostMessage(new { status = 404, headers = new Dictionary<string, string>(), body = "Not found" });
                return;
            }

            // Find the torrent
            var swarm = FindSwarm(infoHash);
            if (swarm == null || swarm.Files == null || fileIdx < 0 || fileIdx >= swarm.Files.Length)
            {
                port.PostMessage(new { status = 404, headers = new Dictionary<string, string>(), body = "Torrent not found" });
                return;
            }

            var file = swarm.Files[fileIdx];
            var totalSize = file.Length;
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            var contentType = MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";

            // Parse range header
            long rangeStart = 0;
            long rangeEnd = totalSize - 1;
            bool isRange = false;
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                isRange = true;
                var parts = rangeHeader.Substring(6).Split('-');
                if (parts.Length >= 1 && long.TryParse(parts[0], out var rs)) rangeStart = rs;
                if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out var re)) rangeEnd = re;
                if (rangeEnd >= totalSize) rangeEnd = totalSize - 1;
            }

            var length = (int)(rangeEnd - rangeStart + 1);

            // Build response headers
            var responseHeaders = new Dictionary<string, string>
            {
                ["Content-Type"] = contentType,
                ["Accept-Ranges"] = "bytes",
                ["Cache-Control"] = "no-cache, no-store, must-revalidate, max-age=0",
                ["Content-Length"] = length.ToString(),
            };
            if (isRange)
                responseHeaders["Content-Range"] = $"bytes {rangeStart}-{rangeEnd}/{totalSize}";

            int status = isRange ? 206 : 200;

            // Send STREAM response — SW will set up ReadableStream and pull chunks
            port.PostMessage(new { status, headers = responseHeaders, body = "STREAM", destination });

            // Set up pull-based streaming
            var opfsStore = swarm.Store as AsyncFSChunkStore;
            var metadata = swarm.Metadata!;
            var streamState = new StreamState
            {
                Port = port,
                File = file,
                Metadata = metadata,
                OpfsStore = opfsStore,
                Offset = rangeStart,
                Remaining = length,
            };
            streamState.Handler = streamState.HandlePull;
            port.OnMessage += streamState.Handler;
        }
        catch { }
    }

    private TorrentSwarm? FindSwarm(string infoHashHex)
    {
        foreach (var swarm in _client.Torrents)
        {
            if (!swarm.HasMetadata) continue;
            var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
            if (hash == infoHashHex) return swarm;
        }
        return null;
    }

    private static bool TryParseStreamUrl(string requestUrl, out string infoHash, out int fileIdx)
    {
        infoHash = "";
        fileIdx = -1;
        try
        {
            var url = new Uri(requestUrl);
            var segments = url.AbsolutePath.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray();
            var wtIdx = System.Array.IndexOf(segments, "webtorrent");
            if (wtIdx < 0 || wtIdx + 2 >= segments.Length) return false;
            infoHash = segments[wtIdx + 1];
            return int.TryParse(segments[wtIdx + 2], out fileIdx);
        }
        catch { return false; }
    }

    /// <summary>
    /// Get the streaming URL for a torrent file.
    /// Point a video/audio/img element's src at this URL for streaming with seeking.
    /// </summary>
    public static string GetStreamUrl(TorrentSwarm swarm, int fileIndex)
    {
        var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
        return $"/webtorrent/{hash}/{fileIndex}";
    }

    /// <summary>
    /// Get the streaming URL for a torrent file.
    /// </summary>
    public static string GetStreamUrl(TorrentSwarm swarm, TorrentFileStream file)
    {
        var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
        var fileIdx = System.Array.IndexOf(swarm.Files!, file);
        return $"/webtorrent/{hash}/{fileIdx}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!OperatingSystem.IsBrowser()) return;
        try
        {
            var swContainer = BlazorJSRuntime.JS.Get<ServiceWorkerContainer>("navigator.serviceWorker");
            if (swContainer == null) return;
            swContainer.OnMessage -= _handler;
            swContainer.Dispose();
        }
        catch { }
    }

    private class StreamState
    {
        public MessagePort Port { get; set; } = null!;
        public TorrentFileStream File { get; set; } = null!;
        public TorrentMetadata Metadata { get; set; } = null!;
        public AsyncFSChunkStore? OpfsStore { get; set; }
        public long Offset { get; set; }
        public int Remaining { get; set; }
        public Action<MessageEvent>? Handler { get; set; }
        private const int ChunkSize = 65536;

        public void HandlePull(MessageEvent pullMsg)
        {
            var pullData = pullMsg.GetData<bool>();
            if (!pullData)
            {
                Cleanup();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (Remaining <= 0)
                    {
                        Port.PostMessage(null);
                        Cleanup();
                        return;
                    }

                    var toRead = (int)Math.Min(ChunkSize, Remaining);
                    var pieceIdx = (int)(Offset / Metadata.PieceLength);
                    var pieceOffset = (int)(Offset % Metadata.PieceLength);

                    // Zero-copy path: read piece as Uint8Array directly from OPFS
                    if (OpfsStore != null && OpfsStore.SupportsUint8Array)
                    {
                        using var pieceUint8 = await OpfsStore.GetUint8ArrayAsync(pieceIdx);
                        if (pieceUint8 != null)
                        {
                            var available = (int)pieceUint8.Length - pieceOffset;
                            var sendLen = Math.Min(toRead, available);
                            using var slice = pieceUint8.Slice(pieceOffset, pieceOffset + sendLen);
                            Port.PostMessage(slice);
                            Offset += sendLen;
                            Remaining -= sendLen;
                            return;
                        }
                    }

                    // Fallback: read through .NET byte[]
                    var chunk = await File.ReadAsync(Offset, toRead);
                    Offset += chunk.Length;
                    Remaining -= chunk.Length;
                    using var uint8 = new Uint8Array(chunk);
                    Port.PostMessage(uint8);
                }
                catch
                {
                    Port.PostMessage(null);
                    Cleanup();
                }
            });
        }

        private void Cleanup()
        {
            if (Handler != null)
            {
                Port.OnMessage -= Handler;
                Handler = null;
            }
        }
    }
}
