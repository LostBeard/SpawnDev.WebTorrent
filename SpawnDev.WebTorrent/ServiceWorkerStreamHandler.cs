using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

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

        _swContainer = SpawnJSRuntime.Instance.Get<ServiceWorkerContainer>("navigator.serviceWorker");
        if (_swContainer == null) return Task.CompletedTask;
        _swContainer.OnMessage += HandleMessage;
        if (WebTorrentClient.VerboseLogging) Console.WriteLine("[WebTorrent SW Handler] Initialized — listening for SW messages");
        return Task.CompletedTask;
    }

    private void HandleMessage(MessageEvent msgEvent)
    {
        try
        {
            using var data = msgEvent.GetData<SpawnJSObject>();
            var msgType = data.JSRef!.Get<string?>("type");
            if (msgType != "webtorrent") return;

            var requestUrl = data.JSRef!.Get<string>("url") ?? "";
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WebTorrent SW Handler] Received request: {requestUrl}");
            using var headersObj = data.JSRef!.Get<SpawnJSObject?>("headers");
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

    /// <summary>Diagnostic: total bytes a stream actually delivered, keyed by its start offset. Lets a test
    /// tell whether the &lt;video&gt; element pulled (and received) its tail/moov range vs never reading it.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> DebugBytesByStartOffset = new();

    /// <summary>Diagnostic: per-pull read-flow log (which stream by start offset, the file offset read, bytes
    /// requested, bytes returned, posted/EOF/cancel). Lets a test see EXACTLY what each stream's pulls did —
    /// distinguishes "the &lt;video&gt; never pulled the tail" from "it pulled and the read returned 0 bytes".</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> DebugStreamLog = new();
    private static void DbgLog(string s) { DebugStreamLog.Enqueue(s); while (DebugStreamLog.Count > 160) DebugStreamLog.TryDequeue(out _); }

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

        // Open-ended bytes=N- serves through end-of-file (rangeEnd = totalSize-1) — identical to the working
        // reference (RequestRange.ParseRange: open-ended end = contentLength-1). The element gets the full
        // extent and seeks the tail moov; the stream is made cancellable/idle-draining (see SW + StreamState)
        // so an abandoned front releases Chromium's data source.
        var length = rangeEnd - rangeStart + 1;

        // Prioritize THIS request's pieces by INVERSE range size. A non-faststart MP4 keeps its moov at the
        // end, so the <video> issues a small tail range (e.g. bytes=128614400-) to read metadata; giving a
        // small range high priority makes the picker fetch the moov ahead of the large front read instead of
        // waiting for the whole file. The whole-file selection (priority 1, from EnsureReadSelection on the
        // read path) keeps downloading in the background. Mirrors SpawnDev.com's TorrentFSExtension pattern;
        // our Selections is already priority-ordered (high first) — we just feed the streaming range into it.
        int selStart = file.StartPiece, selEnd = file.EndPiece;
        if (torrent.PieceLength > 0)
        {
            selStart = file.StartPiece + (int)(rangeStart / torrent.PieceLength);
            selEnd = file.StartPiece + (int)(rangeEnd / torrent.PieceLength);
            if (selStart < file.StartPiece) selStart = file.StartPiece;
            if (selEnd > file.EndPiece) selEnd = file.EndPiece;
            long priority = totalSize + 2 - length;   // small range => high priority (tail moov wins)
            torrent.Select(selStart, selEnd, (int)Math.Min(priority, int.MaxValue), isStreamSelection: true);
            // Also mark the immediate read need CRITICAL up front so it downloads in PARALLEL right now,
            // independent of how small the element chunks its pulls (Chromium reads the moov in ~64 KB pulls,
            // so a per-pull critical mark only fetches ~1 piece at a time and the moov trickles in). For a
            // small tail-moov range this covers the WHOLE moov, so loadedmetadata isn't gated on its last
            // piece arriving piece-by-piece; for a large open-ended range (the front read) only the first few
            // pieces, so it can't starve the moov. critical-first picking is priority-ordered, so the moov
            // (high-priority selection) still wins over the front's critical pieces.
            int rangePieces = selEnd - selStart + 1;
            int critEnd = rangePieces <= 64 ? selEnd : Math.Min(selStart + 7, selEnd);
            torrent.Critical(selStart, critEnd);
        }

        var responseHeaders = new Dictionary<string, string>
        {
            ["Content-Type"] = contentType,
            ["Accept-Ranges"] = "bytes",
            // No Cache-Control: media elements rely on the browser caching served byte ranges for seeking;
            // the previous "no-store" forbade that. Matches SpawnDev.com's working FSExtensionBase media path.
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
        var streamState = new StreamState(Port, torrent, fileIndex, rangeStart, (int)Math.Min(length, int.MaxValue), selStart, selEnd);
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
        private readonly long _startOffset;
        private int _remaining;
        private readonly int _startPiece;          // this stream's piece range (released on teardown)
        private readonly int _endPiece;
        private readonly CancellationTokenSource _cts = new();   // cancels the blocking read when the video seeks away
        private const int ChunkSize = 65536;

        public StreamState(MessagePort port, Torrent torrent, int fileIndex, long startOffset, int length, int startPiece, int endPiece)
        {
            _port = port;
            _torrent = torrent;
            _fileIndex = fileIndex;
            _offset = startOffset;
            _startOffset = startOffset;
            _remaining = length;
            _startPiece = startPiece;
            _endPiece = endPiece;
            DbgLog($"[{startOffset}] NEW len={length} pieces={startPiece}-{endPiece}");
        }

        public void HandlePull(MessageEvent pullMsg)
        {
            using var pullData = pullMsg.GetData<SpawnJSObject>();
            var eventType = pullData.JSRef!.Get<string>("eventType");

            if (eventType == "cancel" || eventType == "error")
            {
                DbgLog($"[{_startOffset}] {eventType} (delivered={_offset - _startOffset})");
                _cts.Cancel();   // abort any in-flight EnsurePieceAsync so a seek can't wedge the read
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
                        DbgLog($"[{_startOffset}] pull@{_offset} done-rem0");
                        _port.PostMessage("");
                        Cleanup();
                        return;
                    }

                    var toRead = Math.Min(ChunkSize, _remaining);
                    var readOff = _offset;
                    // Zero-copy: read the chunk straight into a JS Uint8Array and hand it back to the
                    // service worker WITHOUT the bytes ever entering the .NET/WASM heap. On an OPFS-backed
                    // store the piece data stays JS-side end to end — this is TJ's design: the window client
                    // fulfills the SW's torrent-chunk request via ReadFileUint8ArrayAsync, no byte[] hop.
                    using var uint8 = await _torrent.ReadFileUint8ArrayAsync(_fileIndex, _offset, toRead, _cts.Token);
                    var read = (int)uint8.Length;

                    if (read == 0)
                    {
                        DbgLog($"[{_startOffset}] pull@{readOff} toRead={toRead} got=0 -> EOF");
                        _port.PostMessage("");
                        Cleanup();
                        return;
                    }

                    _remaining -= read;
                    _offset += read;

                    _port.PostMessage(uint8);
                    DebugBytesByStartOffset.AddOrUpdate(_startOffset, read, (_, v) => v + read);
                    DbgLog($"[{_startOffset}] pull@{readOff} toRead={toRead} got={read} posted rem={_remaining}");
                }
                catch (OperationCanceledException)
                {
                    DbgLog($"[{_startOffset}] pull canceled @ {_offset}");   // seek aborted this read; expected
                }
                catch (Exception ex)
                {
                    DbgLog($"[{_startOffset}] pull ERR {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"[WebTorrent SW Handler] Stream chunk error: {ex}");
                    _port.PostMessage("");
                    Cleanup();
                }
            });
        }

        private void Cleanup()
        {
            _port.OnMessage -= HandlePull;
            try { _cts.Cancel(); } catch { }
            // NOTE: deliberately do NOT Deselect this stream's pieces here. A media element seeking a
            // non-faststart moov rapidly opens+cancels the SAME tail range several times; deselecting on each
            // cancel kept yanking the high-priority moov range out of the picker mid-download, so the tail
            // piece it needs never finished and the element thrashed. The stream selection is low-cost to
            // leave in place (it's high priority, so the picker completes it fast, then moves on).
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
