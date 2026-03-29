using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Service worker stream handler — DI singleton, IAsyncBackgroundService.
/// Starts with the app, listens for 'webtorrent' messages from the service worker,
/// and routes them to registered request handlers.
///
/// Registration:
///   builder.Services.AddSingleton&lt;ServiceWorkerStreamHandler&gt;();
///
/// Any service can register to handle requests for specific paths,
/// or listen to the OnRequest event for custom handling.
/// </summary>
public class ServiceWorkerStreamHandler : IAsyncBackgroundService, IDisposable
{
    public Task Ready => _ready ??= InitAsync();
    private Task? _ready;
    private bool _disposed;

    /// <summary>Fired when a streaming request is received from the service worker.</summary>
    public event Action<StreamRequest>? OnRequest;

    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mkv"] = "video/x-matroska",
        [".ogv"] = "video/ogg", [".mov"] = "video/quicktime",
        [".mp3"] = "audio/mpeg", [".ogg"] = "audio/ogg", [".flac"] = "audio/flac",
        [".wav"] = "audio/wav", [".aac"] = "audio/aac", [".opus"] = "audio/opus",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    private Task InitAsync()
    {
        if (!OperatingSystem.IsBrowser()) return Task.CompletedTask;

        var swContainer = BlazorJSRuntime.JS.Get<ServiceWorkerContainer>("navigator.serviceWorker");
        if (swContainer == null) return Task.CompletedTask;
        swContainer.OnMessage += HandleMessage;
        return Task.CompletedTask;
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

            // Parse URL
            if (!TryParseStreamUrl(requestUrl, out var infoHash, out var fileIdx))
            {
                port.PostMessage(new { status = 404, headers = new Dictionary<string, string>(), body = "Not found" });
                return;
            }

            // Create request object and let handlers respond
            var request = new StreamRequest
            {
                Url = requestUrl,
                InfoHash = infoHash,
                FileIndex = fileIdx,
                RangeHeader = rangeHeader,
                Destination = destination,
                Port = port,
            };

            OnRequest?.Invoke(request);

            if (!request.Handled)
            {
                port.PostMessage(new { status = 404, headers = new Dictionary<string, string>(), body = "No handler for this request" });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebTorrent SW Handler] HandleMessage error: {ex}");
        }
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebTorrent SW Handler] TryParseStreamUrl error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get the MIME type for a file extension.
    /// </summary>
    public static string GetMimeType(string ext)
        => MimeTypes.TryGetValue(ext.ToLowerInvariant(), out var mime) ? mime : "application/octet-stream";

    /// <summary>
    /// Get the streaming URL for a torrent file.
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
            swContainer.OnMessage -= HandleMessage;
            swContainer.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebTorrent SW Handler] Dispose error: {ex.Message}");
        }
    }
}

/// <summary>
/// A streaming request from the service worker.
/// Handlers set Handled = true after responding via the Port.
/// </summary>
public class StreamRequest
{
    public string Url { get; set; } = "";
    public string InfoHash { get; set; } = "";
    public int FileIndex { get; set; }
    public string? RangeHeader { get; set; }
    public string Destination { get; set; } = "";
    public MessagePort Port { get; set; } = null!;
    public bool Handled { get; set; }

    /// <summary>
    /// Respond with a STREAM for the given file, supporting range requests.
    /// Call this from your OnRequest handler.
    /// </summary>
    public void RespondWithStream(TorrentFileStream file, TorrentMetadata metadata, AsyncFSChunkStore? opfsStore = null)
    {
        Handled = true;
        var totalSize = file.Length;
        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        var contentType = ServiceWorkerStreamHandler.GetMimeType(ext);

        long rangeStart = 0;
        long rangeEnd = totalSize - 1;
        bool isRange = false;
        if (!string.IsNullOrEmpty(RangeHeader) && RangeHeader.StartsWith("bytes="))
        {
            isRange = true;
            var parts = RangeHeader.Substring(6).Split('-');
            if (parts.Length >= 1 && long.TryParse(parts[0], out var rs)) rangeStart = rs;
            if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out var re)) rangeEnd = re;
            if (rangeEnd >= totalSize) rangeEnd = totalSize - 1;
        }

        var length = (int)(rangeEnd - rangeStart + 1);

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
        Port.PostMessage(new { status, headers = responseHeaders, body = "STREAM", destination = Destination });

        // Pull-based chunk streaming
        var streamState = new StreamState
        {
            Port = Port,
            File = file,
            Metadata = metadata,
            OpfsStore = opfsStore,
            Offset = rangeStart,
            Remaining = length,
        };
        streamState.Handler = streamState.HandlePull;
        Port.OnMessage += streamState.Handler;
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

                    var chunk = await File.ReadAsync(Offset, toRead);
                    Offset += chunk.Length;
                    Remaining -= chunk.Length;
                    using var uint8 = new Uint8Array(chunk);
                    Port.PostMessage(uint8);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WebTorrent SW Handler] Stream chunk error: {ex}");
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
