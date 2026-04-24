using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;

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
    private ServiceWorkerContainer? _swContainer;
    private bool _disposed;

    /// <summary>Fired when a streaming request is received from the service worker.</summary>
    public event Action<StreamRequest>? OnRequest;

    private Task InitAsync()
    {
        if (!OperatingSystem.IsBrowser()) return Task.CompletedTask;

        _swContainer = BlazorJSRuntime.JS.Get<ServiceWorkerContainer>("navigator.serviceWorker");
        if (_swContainer == null) return Task.CompletedTask;
        _swContainer.OnMessage += HandleMessage;
        if (WebTorrentClient.VerboseLogging) Console.WriteLine("[WebTorrent SW Handler] Initialized — listening for SW messages");
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
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WebTorrent SW Handler] Received request: {requestUrl}");
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

            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WebTorrent SW Handler] Firing OnRequest: hash={infoHash}, fileIdx={fileIdx}, subscribers={OnRequest?.GetInvocationList().Length ?? 0}");
            OnRequest?.Invoke(request);
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WebTorrent SW Handler] OnRequest done: handled={request.Handled}");

            if (!request.Handled)
            {
                port.PostMessage(new { status = 404, headers = new Dictionary<string, string>(), body = "No handler for this request" });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebTorrent SW Handler] HandleMessage error: {ex}");
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
            Console.WriteLine($"[WebTorrent SW Handler] TryParseStreamUrl error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get the streaming URL for a torrent file by index. Uses
    /// <see cref="Torrent.WireInfoHashHex"/> so pure-v2 torrents (empty v1
    /// <see cref="Torrent.InfoHash"/>) route through their 20-byte wire prefix
    /// instead of breaking the URL with an empty path segment.
    /// </summary>
    public static string GetStreamUrl(Torrent torrent, int fileIndex)
    {
        return $"/webtorrent/{torrent.WireInfoHashHex}/{fileIndex}";
    }

    /// <summary>
    /// Get the streaming URL for a torrent file by TorrentFileInfo reference.
    /// </summary>
    public static string GetStreamUrl(Torrent torrent, TorrentFileInfo file)
    {
        var fileIdx = torrent.Files != null ? System.Array.IndexOf(torrent.Files, file) : -1;
        return $"/webtorrent/{torrent.WireInfoHashHex}/{fileIdx}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!OperatingSystem.IsBrowser()) return;
        try
        {
            if (_swContainer == null) return;
            _swContainer.OnMessage -= HandleMessage;
            _swContainer.Dispose();
            _swContainer = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebTorrent SW Handler] Dispose error: {ex.Message}");
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
    /// Respond with a STREAM for the given torrent file, supporting range requests.
    /// Uses torrent.ReadFileAsync() which works during download (waits for pieces).
    /// Call this from your OnRequest handler.
    /// </summary>
    public void RespondWithStream(Torrent torrent, int fileIndex)
    {
        Handled = true;
        if (torrent.Files == null || fileIndex < 0 || fileIndex >= torrent.Files.Length)
        {
            Port.PostMessage(new { status = 404, headers = new Dictionary<string, string>(), body = "File not found" });
            return;
        }

        var file = torrent.Files[fileIndex];
        var totalSize = file.Length;
        var contentType = file.Type;

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

        var length = rangeEnd - rangeStart + 1;

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
        var response = new FSExtensionResponse
        {
            status = status,
            headers = responseHeaders,
            body = "stream_pull",
        };

        // Wire up pull handler FIRST, then Start, then PostMessage (order matters)
        var streamState = new StreamState(Port, torrent, fileIndex, rangeStart, (int)Math.Min(length, int.MaxValue));
        Port.OnMessage += streamState.HandlePull;
        Port.Start();
        Port.PostMessage(response);
    }

    /// <summary>
    /// Respond with a STREAM for the given torrent file, supporting range requests.
    /// Convenience overload that takes the TorrentFileInfo directly.
    /// </summary>
    public void RespondWithStream(Torrent torrent, TorrentFileInfo file)
    {
        var fileIdx = torrent.Files != null ? System.Array.IndexOf(torrent.Files, file) : -1;
        RespondWithStream(torrent, fileIdx);
    }

    private class StreamState
    {
        private readonly MessagePort _port;
        private readonly Torrent _torrent;
        private readonly int _fileIndex;
        private long _offset;
        private int _remaining;
        private const int ChunkSize = 65536;

        public StreamState(MessagePort port, Torrent torrent, int fileIndex, long startOffset, int length)
        {
            _port = port;
            _torrent = torrent;
            _fileIndex = fileIndex;
            _offset = startOffset;
            _remaining = length;
        }

        public void HandlePull(MessageEvent pullMsg)
        {
            using var pullData = pullMsg.GetData<JSObject>();
            var eventType = pullData.JSRef!.Get<string>("eventType");

            if (eventType == "cancel" || eventType == "error")
            {
                Cleanup();
                return;
            }

            if (eventType != "pull") return;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_remaining <= 0)
                    {
                        _port.PostMessage("");
                        Cleanup();
                        return;
                    }

                    var toRead = Math.Min(ChunkSize, _remaining);
                    var data = await _torrent.ReadFileAsync(_fileIndex, _offset, toRead);

                    if (data == null || data.Length == 0)
                    {
                        _port.PostMessage("");
                        Cleanup();
                        return;
                    }

                    _remaining -= data.Length;
                    _offset += data.Length;

                    using var uint8 = new Uint8Array(data);
                    _port.PostMessage(uint8);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebTorrent SW Handler] Stream chunk error: {ex}");
                    _port.PostMessage("");
                    Cleanup();
                }
            });
        }

        private void Cleanup()
        {
            _port.OnMessage -= HandlePull;
        }
    }

    /// <summary>Response object matching the service-worker-fs.js FSExtensionResponse protocol.</summary>
    private class FSExtensionResponse
    {
        public int status { get; set; }
        public Dictionary<string, string> headers { get; set; } = new();
        public string body { get; set; } = "";
    }
}
