using System.Text;
using System.Web;

namespace SpawnDev.WebTorrent.Discovery;

/// <summary>
/// HTTP/HTTPS tracker client. Works on both desktop and browser.
/// Sends announces via HTTP GET with URL-encoded parameters.
/// Parses bencoded compact peer lists from the response.
/// </summary>
public class HttpTrackerClient : IDiscovery
{
    private readonly string _announceUrl;
    private readonly byte[] _peerId;
    private HttpClient? _httpClient;
    private byte[]? _currentInfoHash;

    public string Type => "http-tracker";
    public bool IsConnected { get; private set; }

    public event Action<PeerInfo>? OnPeer;
    public event Action<int, int>? OnAnnounceResponse;
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    public HttpTrackerClient(string announceUrl, byte[] peerId)
    {
        _announceUrl = announceUrl;
        _peerId = peerId;
    }

    public async Task StartAsync(byte[] infoHash, int port, CancellationToken ct = default)
    {
        _currentInfoHash = infoHash;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        try
        {
            await AnnounceAsync(infoHash, port, 0, 0, 0, ct);
            IsConnected = true;
            OnConnected?.Invoke();
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"HTTP tracker error: {ex.Message}");
        }
    }

    public async Task AnnounceAsync(byte[] infoHash, int port,
        long uploaded, long downloaded, long left, CancellationToken ct = default)
    {
        if (_httpClient == null) return;

        try
        {
            // Build URL with query parameters
            var url = _announceUrl;
            var sep = url.Contains('?') ? '&' : '?';

            url += $"{sep}info_hash={UrlEncodeBytes(infoHash)}";
            url += $"&peer_id={UrlEncodeBytes(_peerId)}";
            url += $"&port={port}";
            url += $"&uploaded={uploaded}";
            url += $"&downloaded={downloaded}";
            url += $"&left={left}";
            url += "&compact=1";
            url += "&numwant=80";

            var response = await _httpClient.GetByteArrayAsync(url, ct);

            // Parse bencoded response
            var (decoded, _) = Bencode.BencodeDecoder.Decode(response, 0);
            if (decoded is not Dictionary<string, object> dict) return;

            // Check for error
            if (dict.TryGetValue("failure reason", out var failObj) && failObj is byte[] failBytes)
            {
                OnError?.Invoke(Encoding.UTF8.GetString(failBytes));
                return;
            }

            // Parse seeders/leechers
            int seeders = dict.TryGetValue("complete", out var c) && c is long cl ? (int)cl : 0;
            int leechers = dict.TryGetValue("incomplete", out var ic) && ic is long icl ? (int)icl : 0;
            OnAnnounceResponse?.Invoke(seeders, leechers);

            // Parse compact peer list (6 bytes per IPv4 peer)
            if (dict.TryGetValue("peers", out var peersObj))
            {
                byte[]? peersBytes = null;
                if (peersObj is byte[] pb) peersBytes = pb;
                else if (peersObj is string ps) peersBytes = Encoding.Latin1.GetBytes(ps);

                if (peersBytes != null)
                {
                    for (int i = 0; i + 6 <= peersBytes.Length; i += 6)
                    {
                        var ip = $"{peersBytes[i]}.{peersBytes[i + 1]}.{peersBytes[i + 2]}.{peersBytes[i + 3]}";
                        var peerPort = (peersBytes[i + 4] << 8) | peersBytes[i + 5];
                        OnPeer?.Invoke(new PeerInfo { Address = $"{ip}:{peerPort}", Source = "http-tracker" });
                    }
                }

                // Also handle non-compact (list of dicts)
                if (peersObj is List<object> peerList)
                {
                    foreach (var peerObj in peerList)
                    {
                        if (peerObj is Dictionary<string, object> peerDict)
                        {
                            var ip = peerDict.TryGetValue("ip", out var ipObj) && ipObj is byte[] ipBytes
                                ? Encoding.UTF8.GetString(ipBytes) : null;
                            var peerPort = peerDict.TryGetValue("port", out var portObj) && portObj is long pl
                                ? (int)pl : 0;
                            if (ip != null && peerPort > 0)
                                OnPeer?.Invoke(new PeerInfo { Address = $"{ip}:{peerPort}", Source = "http-tracker" });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Announce failed: {ex.Message}");
        }
    }

    public Task StopAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        IsConnected = false;
        OnDisconnected?.Invoke();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>URL-encode raw bytes (for info_hash and peer_id).</summary>
    private static string UrlEncodeBytes(byte[] bytes)
    {
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                || b == '-' || b == '_' || b == '.' || b == '~')
                sb.Append((char)b);
            else
                sb.Append($"%{b:X2}");
        }
        return sb.ToString();
    }
}
