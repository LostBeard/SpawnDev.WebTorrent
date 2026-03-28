namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// Web seed connection — downloads pieces via HTTP range requests (BEP 17/19).
/// </summary>
public class WebSeedConnection
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly Torrent.TorrentMetadata _metadata;

    public bool IsAvailable { get; private set; } = true;
    public int FailureCount { get; private set; }
    public int MaxConcurrent { get; set; } = 4;
    private int _activeRequests;
    private DateTime _backoffUntil = DateTime.MinValue;

    /// <summary>Diagnostic log event.</summary>
    public event Action<string>? OnLog;

    public WebSeedConnection(HttpClient httpClient, string baseUrl, Torrent.TorrentMetadata metadata)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _metadata = metadata;
    }

    public async Task<byte[]?> DownloadPieceAsync(int pieceIndex, CancellationToken ct = default)
    {
        // Auto-recover after backoff period
        if (!IsAvailable && DateTime.UtcNow > _backoffUntil)
        {
            IsAvailable = true;
            FailureCount = 0;
            OnLog?.Invoke("Web seed recovered from backoff");
        }

        if (!IsAvailable || _activeRequests >= MaxConcurrent) return null;

        try
        {
            Interlocked.Increment(ref _activeRequests);

            long pieceStart = (long)pieceIndex * _metadata.PieceLength;
            int pieceLength = (pieceIndex == _metadata.PieceCount - 1)
                ? (int)(_metadata.TotalLength - pieceStart)
                : _metadata.PieceLength;

            // For multi-file torrents, a piece may span multiple files.
            // We need to download from each file that overlaps this piece range
            // and assemble the complete piece.
            var pieceData = new byte[pieceLength];
            int filled = 0;
            long currentOffset = pieceStart;

            while (filled < pieceLength)
            {
                // Find which file contains currentOffset
                Torrent.TorrentFile? targetFile = null;
                foreach (var file in _metadata.Files)
                {
                    if (currentOffset >= file.Offset && currentOffset < file.Offset + file.Length)
                    {
                        targetFile = file;
                        break;
                    }
                }

                if (targetFile == null)
                {
                    OnLog?.Invoke($"No file found for offset {currentOffset}");
                    return null;
                }

                // Calculate byte range within this file
                long fileOffset = currentOffset - targetFile.Offset;
                long bytesAvailInFile = targetFile.Length - fileOffset;
                int bytesToRead = (int)Math.Min(bytesAvailInFile, pieceLength - filled);
                long rangeStart = fileOffset;
                long rangeEnd = fileOffset + bytesToRead - 1;

                // Build URL for this file
                string url;
                if (_metadata.Files.Length == 1 && !_metadata.Files[0].Path.Contains('/'))
                    url = $"{_baseUrl}/{EscapePath(_metadata.Name)}";
                else
                    url = $"{_baseUrl}/{EscapePath(targetFile.Path)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(rangeStart, rangeEnd);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    FailureCount++;
                    OnLog?.Invoke($"HTTP error {(int)response.StatusCode} (failure #{FailureCount})");
                    if (FailureCount >= 10)
                    {
                        IsAvailable = false;
                        _backoffUntil = DateTime.UtcNow.AddSeconds(30);
                    }
                    return null;
                }

                var data = await response.Content.ReadAsByteArrayAsync(ct);

                // Handle 200 OK (full file) — extract the range we need
                if (response.StatusCode == System.Net.HttpStatusCode.OK && data.Length > bytesToRead)
                {
                    if (rangeStart + bytesToRead <= data.Length)
                    {
                        Array.Copy(data, rangeStart, pieceData, filled, bytesToRead);
                    }
                    else
                    {
                        OnLog?.Invoke($"Full file too small: {data.Length} < {rangeStart + bytesToRead}");
                        return null;
                    }
                }
                else
                {
                    if (data.Length < bytesToRead)
                    {
                        OnLog?.Invoke($"Short read: got {data.Length}, expected {bytesToRead}");
                        return null;
                    }
                    Array.Copy(data, 0, pieceData, filled, bytesToRead);
                }

                filled += bytesToRead;
                currentOffset += bytesToRead;
            }

            FailureCount = 0;
            return pieceData;
        }
        catch (Exception ex)
        {
            FailureCount++;
            OnLog?.Invoke($"Exception: {ex.GetType().Name}: {ex.Message} (failure #{FailureCount})");
            if (FailureCount >= 10)
            {
                IsAvailable = false;
                _backoffUntil = DateTime.UtcNow.AddSeconds(30);
                OnLog?.Invoke("Web seed backing off for 30 seconds");
            }
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

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
        catch { return null; }
    }

    public void Reset()
    {
        FailureCount = 0;
        IsAvailable = true;
    }

    private string BuildUrl(int pieceIndex, long start, long end)
    {
        // BEP 19 (GetRight-style): baseUrl/torrentName (single file) or baseUrl/torrentName/filePath (multi-file)
        // Path segments must be individually escaped (preserve / separators)
        if (_metadata.Files.Length == 1 && !_metadata.Files[0].Path.Contains('/'))
        {
            // Single file torrent — URL is baseUrl/name
            return $"{_baseUrl}/{EscapePath(_metadata.Name)}";
        }

        // Multi-file torrent — find which file this piece range falls in
        // The URL is baseUrl/filePath (the path already includes the torrent name as a directory)
        foreach (var file in _metadata.Files)
        {
            if (start >= file.Offset && start < file.Offset + file.Length)
                return $"{_baseUrl}/{EscapePath(file.Path)}";
        }

        return $"{_baseUrl}/{EscapePath(_metadata.Name)}";
    }

    /// <summary>Escape a file path for URL while preserving / separators.</summary>
    private static string EscapePath(string path)
    {
        return string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
    }
}
