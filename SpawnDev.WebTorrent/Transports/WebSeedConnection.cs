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
        if (!IsAvailable || _activeRequests >= MaxConcurrent) return null;

        try
        {
            Interlocked.Increment(ref _activeRequests);

            long pieceStart = (long)pieceIndex * _metadata.PieceLength;
            int pieceLength = (pieceIndex == _metadata.PieceCount - 1)
                ? (int)(_metadata.TotalLength - pieceStart)
                : _metadata.PieceLength;
            long pieceEnd = pieceStart + pieceLength - 1;

            var url = BuildUrl(pieceIndex, pieceStart, pieceEnd);
            OnLog?.Invoke($"GET {url} Range: bytes={pieceStart}-{pieceEnd}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(pieceStart, pieceEnd);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            OnLog?.Invoke($"Response: {(int)response.StatusCode} {response.StatusCode}, Content-Length: {response.Content.Headers.ContentLength}");

            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                FailureCount++;
                OnLog?.Invoke($"HTTP error {(int)response.StatusCode} (failure #{FailureCount})");
                if (FailureCount >= 10) IsAvailable = false;
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(ct);
            OnLog?.Invoke($"Received {data.Length} bytes (expected {pieceLength})");

            // For 200 OK (full file), extract just the piece we need
            if (response.StatusCode == System.Net.HttpStatusCode.OK && data.Length > pieceLength)
            {
                OnLog?.Invoke($"Server returned full file ({data.Length} bytes), extracting piece range");
                var pieceData = new byte[pieceLength];
                Array.Copy(data, pieceStart, pieceData, 0, pieceLength);
                data = pieceData;
            }

            if (data.Length != pieceLength)
            {
                if (data.Length < pieceLength && pieceIndex != _metadata.PieceCount - 1)
                {
                    OnLog?.Invoke($"Size mismatch: got {data.Length}, expected {pieceLength}");
                    return null;
                }
            }

            FailureCount = 0;
            return data;
        }
        catch (Exception ex)
        {
            FailureCount++;
            OnLog?.Invoke($"Exception: {ex.GetType().Name}: {ex.Message} (failure #{FailureCount})");
            if (FailureCount >= 10) IsAvailable = false;
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
        if (_metadata.Files.Length == 1)
            return $"{_baseUrl}/{Uri.EscapeDataString(_metadata.Files[0].Path)}";

        foreach (var file in _metadata.Files)
        {
            if (start >= file.Offset && start < file.Offset + file.Length)
                return $"{_baseUrl}/{Uri.EscapeDataString(file.Path)}";
        }

        return $"{_baseUrl}/{Uri.EscapeDataString(_metadata.Name)}";
    }
}
