namespace SpawnDev.WebTorrent;

/// <summary>
/// Web seed connection — converts BitTorrent block requests into HTTP range requests.
/// Direct 1:1 port of webtorrent/lib/webconn.js.
/// Acts as a fake Wire that pretends to have all pieces and fulfills requests via HTTP.
/// </summary>
public class WebConn : IAsyncDisposable
{
    // Constants matching JS exactly
    public const int SocketTimeout = 60_000;   // 60s HTTP timeout
    public const int RetryDelay = 10_000;      // 10s before re-announcing piece after HTTP failure

    // ========================
    // STATE
    // ========================

    public string Url { get; }
    public string ConnId { get; }  // unique ID (same as URL, for dedup)
    public Wire WireInstance { get; }
    public bool Destroyed { get; private set; }

    private readonly Torrent _torrent;
    private readonly HttpClient _http;

    // ========================
    // CONSTRUCTOR
    // ========================

    public WebConn(string url, Torrent torrent, HttpClient http)
    {
        Url = url;
        ConnId = url;
        _torrent = torrent;
        _http = http;

        // Create a Wire that this web seed will drive
        WireInstance = new Wire("webSeed");
        WireInstance.SetKeepAlive(true);

        // When the torrent sends us a handshake, respond with ours
        WireInstance.OnHandshake += (infoHash, peerId, exts) =>
        {
            // Use URL hash as our fake peer ID (matches JS: hash(url, 'hex'))
            var fakeId = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(url)
                )
            ).ToLowerInvariant();
            var infoHashBytes = Convert.FromHexString(infoHash);
            var fakeIdBytes = Convert.FromHexString(fakeId.PadRight(40, '0')[..40]);
            _ = WireInstance.Handshake(infoHashBytes, fakeIdBytes);

            // Send bitfield with all pieces set (we "have" everything)
            if (torrent.Pieces.Length > 0)
            {
                var bitfieldBytes = new byte[(int)Math.Ceiling(torrent.Pieces.Length / 8.0)];
                for (int i = 0; i < torrent.Pieces.Length; i++)
                    bitfieldBytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
                _ = WireInstance.Bitfield(bitfieldBytes);
            }
        };

        // When the torrent says it's interested, unchoke it
        WireInstance.OnInterested += () => _ = WireInstance.Unchoke();

        // When the torrent requests a block, serve it via HTTP range request
        WireInstance.OnRequest += (pieceIndex, offset, length, respond) =>
        {
            _ = HandleRequestAsync(pieceIndex, offset, length, respond);
        };
    }

    // ========================
    // HTTP RANGE REQUEST (matches JS httpRequest exactly)
    // ========================

    internal async Task HandleRequestAsync(int pieceIndex, int offset, int length, Action<Exception?, byte[]?> respond)
    {
        try
        {
            long pieceOffset = (long)pieceIndex * _torrent.PieceLength;
            long rangeStart = pieceOffset + offset;
            long rangeEnd = rangeStart + length - 1;

            byte[] data;

            if (_torrent.Files == null || _torrent.Files.Length <= 1)
            {
                // Single-file torrent: if URL ends with /, append the file name (BEP 19 directory style)
                var fileUrl = Url;
                if (fileUrl.EndsWith('/'))
                {
                    var name = _torrent.Name ?? _torrent.Files?[0]?.Name ?? _torrent.Files?[0]?.Path ?? "";
                    fileUrl = fileUrl + Uri.EscapeDataString(name);
                }
                data = await FetchRangeAsync(fileUrl, rangeStart, rangeEnd);
            }
            else
            {
                // Multi-file torrent: split range across files
                var result = new MemoryStream();
                foreach (var file in _torrent.Files)
                {
                    long fileStart = file.Offset;
                    long fileEnd = fileStart + file.Length - 1;

                    if (fileStart > rangeEnd || fileEnd < rangeStart) continue;

                    // Convert OS path separators to URL slashes and encode each segment
                    var pathSegments = (file.Path ?? file.Name ?? "").Replace('\\', '/').Split('/');
                    var encodedPath = string.Join("/", pathSegments.Select(Uri.EscapeDataString));
                    var fileUrl = Url.TrimEnd('/') + "/" + encodedPath;
                    long start = Math.Max(rangeStart - fileStart, 0);
                    long end = Math.Min(fileEnd - fileStart, rangeEnd - fileStart);

                    var chunk = await FetchRangeAsync(fileUrl, start, end);
                    result.Write(chunk);
                }
                data = result.ToArray();
            }

            respond(null, data);
        }
        catch (Exception ex)
        {
            // On HTTP failure, wait RetryDelay then re-announce the piece
            _ = Task.Run(async () =>
            {
                await Task.Delay(RetryDelay);
                if (!Destroyed)
                    await WireInstance.Have(pieceIndex);
            });

            respond(ex, null);
        }
    }

    private async Task<byte[]> FetchRangeAsync(string url, long start, long end)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };

        using var cts = new CancellationTokenSource(SocketTimeout);
        using var response = await _http.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    // ========================
    // DISPOSE
    // ========================

    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;
        WireInstance.Destroy();
    }
}
