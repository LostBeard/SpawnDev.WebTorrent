namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// Web seed connection — downloads pieces via HTTP range requests (BEP 17/19).
/// Behaves like a peer that has all pieces and responds to requests with HTTP GETs.
/// Works on both desktop and browser (HttpClient is available everywhere).
///
/// This is the CDN fallback path: when no peers have a piece, the web seed
/// fetches it from an HTTP server (HuggingFace, our spawndev.com proxy, etc.).
/// </summary>
public class WebSeedConnection
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly Torrent.TorrentMetadata _metadata;

    /// <summary>Whether this web seed is available.</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>Number of consecutive failures (for backoff).</summary>
    public int FailureCount { get; private set; }

    /// <summary>Maximum concurrent requests to this web seed.</summary>
    public int MaxConcurrent { get; set; } = 4;

    private int _activeRequests;

    public WebSeedConnection(HttpClient httpClient, string baseUrl, Torrent.TorrentMetadata metadata)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _metadata = metadata;
    }

    /// <summary>
    /// Download a specific piece via HTTP range request.
    /// Returns the piece data, or null on failure.
    /// </summary>
    public async Task<byte[]?> DownloadPieceAsync(int pieceIndex, CancellationToken ct = default)
    {
        if (!IsAvailable || _activeRequests >= MaxConcurrent) return null;

        try
        {
            Interlocked.Increment(ref _activeRequests);

            // Calculate byte range for this piece
            long pieceStart = (long)pieceIndex * _metadata.PieceLength;
            int pieceLength = (pieceIndex == _metadata.PieceCount - 1)
                ? (int)(_metadata.TotalLength - pieceStart)
                : _metadata.PieceLength;
            long pieceEnd = pieceStart + pieceLength - 1;

            // For single-file torrents, request the byte range directly
            // For multi-file torrents, we'd need to map piece ranges to file ranges
            var url = BuildUrl(pieceIndex, pieceStart, pieceEnd);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(pieceStart, pieceEnd);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                FailureCount++;
                if (FailureCount >= 3) IsAvailable = false; // back off after 3 failures
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(ct);

            // Verify we got the right amount
            if (data.Length != pieceLength)
            {
                // Partial response — might be OK for last piece, otherwise error
                if (data.Length < pieceLength && pieceIndex != _metadata.PieceCount - 1)
                    return null;
            }

            FailureCount = 0; // reset on success
            return data;
        }
        catch (Exception)
        {
            FailureCount++;
            if (FailureCount >= 3) IsAvailable = false;
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    /// <summary>
    /// Download a specific byte range from a file (for random-access streaming).
    /// </summary>
    public async Task<byte[]?> DownloadRangeAsync(string filePath, long start, long end, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/{filePath}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                return null;

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reset failure state (e.g., after successful tracker announce).</summary>
    public void Reset()
    {
        FailureCount = 0;
        IsAvailable = true;
    }

    private string BuildUrl(int pieceIndex, long start, long end)
    {
        // For single-file torrents: baseUrl/filename
        if (_metadata.Files.Length == 1)
            return $"{_baseUrl}/{_metadata.Files[0].Path}";

        // For multi-file torrents: baseUrl/torrentName/filePath
        // Map the piece to the file(s) it spans
        foreach (var file in _metadata.Files)
        {
            if (start >= file.Offset && start < file.Offset + file.Length)
                return $"{_baseUrl}/{file.Path}";
        }

        return $"{_baseUrl}/{_metadata.Name}";
    }
}
