using System.Net;
using System.Text;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent;

/// <summary>
/// HTTP server that serves torrent file content with range request support.
/// Enables streaming to media players, browsers, or any HTTP client.
///
/// Usage:
///   var server = new TorrentHttpServer(client, port: 8080);
///   server.Start();
///   // Access files at: http://localhost:8080/{infoHash}/{filePath}
///   // Or stream video: &lt;video src="http://localhost:8080/{hash}/movie.mp4"&gt;
///
/// Equivalent to WebTorrent JS client.createServer().
/// Desktop only — browser uses blob URLs instead.
/// </summary>
public class TorrentHttpServer : IAsyncDisposable
{
    private readonly WebTorrentClient _client;
    private readonly HttpListener _listener;
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    /// <summary>Base URL of the server.</summary>
    public string BaseUrl => $"http://localhost:{_port}/";

    /// <summary>Whether the server is running.</summary>
    public bool IsRunning => _listenTask != null;

    public TorrentHttpServer(WebTorrentClient client, int port = 8080)
    {
        _client = client;
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    /// <summary>Start the HTTP server.</summary>
    public void Start()
    {
        if (_listenTask != null) return;
        _cts = new CancellationTokenSource();
        _listener.Start();
        _listenTask = ListenLoopAsync(_cts.Token);
    }

    /// <summary>Stop the HTTP server.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
        _listenTask = null;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequestAsync(context);
            }
        }
        catch (HttpListenerException) { }
        catch (OperationCanceledException) { }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        try
        {
            // CORS
            res.AddHeader("Access-Control-Allow-Origin", "*");

            if (req.HttpMethod == "OPTIONS")
            {
                res.AddHeader("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Range");
                res.StatusCode = 204;
                res.Close();
                return;
            }

            // Parse path: /{infoHash}/{filePath}
            var path = req.Url?.AbsolutePath?.TrimStart('/') ?? "";
            if (string.IsNullOrEmpty(path))
            {
                await ServeIndex(res);
                return;
            }

            var slashIdx = path.IndexOf('/');
            if (slashIdx < 0)
            {
                await ServeTorrentIndex(res, path);
                return;
            }

            var hashStr = path[..slashIdx];
            var filePath = Uri.UnescapeDataString(path[(slashIdx + 1)..]);

            // Find torrent
            var swarm = _client.Get(hashStr);
            if (swarm?.Metadata == null)
            {
                res.StatusCode = 404;
                await WriteText(res, "Torrent not found");
                return;
            }

            // Find file
            var file = swarm.Files.FirstOrDefault(f =>
                f.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (file == null)
            {
                res.StatusCode = 404;
                await WriteText(res, "File not found");
                return;
            }

            await ServeFile(req, res, file);
        }
        catch (Exception ex)
        {
            try
            {
                res.StatusCode = 500;
                await WriteText(res, ex.Message);
            }
            catch { }
        }
    }

    private async Task ServeFile(HttpListenerRequest req, HttpListenerResponse res, TorrentFileStream file)
    {
        var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
        res.ContentType = file.Type;

        // Parse Range header
        long start = 0;
        long end = file.Length - 1;
        bool isRange = false;

        var rangeHeader = req.Headers["Range"];
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
        {
            var rangeStr = rangeHeader["bytes=".Length..];
            var parts = rangeStr.Split('-');
            if (parts.Length == 2)
            {
                if (long.TryParse(parts[0], out var s)) start = s;
                if (!string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out var e)) end = e;
                isRange = true;
            }
        }

        long contentLength = end - start + 1;

        if (isRange)
        {
            res.StatusCode = 206;
            res.AddHeader("Content-Range", $"bytes {start}-{end}/{file.Length}");
        }
        else
        {
            res.StatusCode = 200;
        }

        res.ContentLength64 = contentLength;
        res.AddHeader("Accept-Ranges", "bytes");

        if (req.HttpMethod == "HEAD")
        {
            res.Close();
            return;
        }

        // Stream the data in chunks
        int chunkSize = 65536;
        long pos = start;
        while (pos <= end)
        {
            int readLen = (int)Math.Min(chunkSize, end - pos + 1);
            var data = await file.ReadAsync(pos, readLen);
            await res.OutputStream.WriteAsync(data);
            pos += readLen;
        }

        res.Close();
    }

    private async Task ServeIndex(HttpListenerResponse res)
    {
        res.ContentType = "text/html";
        var sb = new StringBuilder();
        sb.AppendLine("<html><body style='font-family:monospace;background:#0f172a;color:#f1f5f9'>");
        sb.AppendLine("<h2>SpawnDev.WebTorrent Server</h2>");
        sb.AppendLine("<ul>");
        foreach (var torrent in _client.Torrents)
        {
            var hash = Convert.ToHexString(torrent.InfoHash).ToLowerInvariant();
            var name = torrent.Metadata?.Name ?? hash[..8];
            sb.AppendLine($"<li><a href='/{hash}/' style='color:#10b981'>{name}</a></li>");
        }
        sb.AppendLine("</ul></body></html>");
        await WriteText(res, sb.ToString());
    }

    private async Task ServeTorrentIndex(HttpListenerResponse res, string hashStr)
    {
        var swarm = _client.Get(hashStr);
        if (swarm?.Metadata == null)
        {
            res.StatusCode = 404;
            await WriteText(res, "Not found");
            return;
        }

        res.ContentType = "text/html";
        var sb = new StringBuilder();
        sb.AppendLine("<html><body style='font-family:monospace;background:#0f172a;color:#f1f5f9'>");
        sb.AppendLine($"<h2>{swarm.Metadata.Name}</h2>");
        sb.AppendLine("<ul>");
        foreach (var file in swarm.Files)
        {
            var url = $"/{hashStr}/{Uri.EscapeDataString(file.Path)}";
            sb.AppendLine($"<li><a href='{url}' style='color:#10b981'>{file.Path}</a> ({file.Length:N0} bytes)</li>");
        }
        sb.AppendLine("</ul></body></html>");
        await WriteText(res, sb.ToString());
    }

    private static async Task WriteText(HttpListenerResponse res, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
    }
}
