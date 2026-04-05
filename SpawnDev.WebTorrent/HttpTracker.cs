using SpawnDev.WebTorrent.Bencode;
using System.Text;
using System.Web;

namespace SpawnDev.WebTorrent;

/// <summary>
/// HTTP BitTorrent tracker client.
/// Direct 1:1 port of bittorrent-tracker/lib/client/http-tracker.js.
/// </summary>
public class HttpTracker : IAsyncDisposable
{
    public const int DefaultAnnounceInterval = 30_000;
    public const int RequestTimeout = 30_000;

    public string AnnounceUrl { get; }
    public string? ScrapeUrl { get; }
    public bool Destroyed { get; private set; }

    private readonly byte[] _infoHash;
    private readonly byte[] _peerId;
    private readonly HttpClient _http;
    private string? _trackerId;
    private Timer? _announceTimer;

    public event Action<string>? OnPeer;  // "ip:port"
    public event Action<TrackerUpdate>? OnUpdate;
    public event Action<string>? OnWarning;
    public event Action? OnAnnounce;

    public HttpTracker(string announceUrl, byte[] infoHash, byte[] peerId, HttpClient http)
    {
        AnnounceUrl = announceUrl;
        _infoHash = infoHash;
        _peerId = peerId;
        _http = http;

        // Determine scrape URL (replace /announce with /scrape)
        var idx = announceUrl.LastIndexOf("/announce", StringComparison.Ordinal);
        if (idx >= 0)
            ScrapeUrl = announceUrl[..idx] + "/scrape" + announceUrl[(idx + 9)..];
    }

    /// <summary>Send announce to HTTP tracker.</summary>
    public async Task AnnounceAsync(AnnounceOptions opts)
    {
        if (Destroyed) return;

        var sb = new StringBuilder(AnnounceUrl);
        sb.Append(AnnounceUrl.Contains('?') ? '&' : '?');
        sb.Append("info_hash=").Append(UrlEncodeBytes(_infoHash));
        sb.Append("&peer_id=").Append(UrlEncodeBytes(_peerId));
        sb.Append("&port=0");
        sb.Append("&uploaded=").Append(opts.Uploaded);
        sb.Append("&downloaded=").Append(opts.Downloaded);
        sb.Append("&left=").Append(opts.Left > 0 ? opts.Left : 16384);
        sb.Append("&compact=1");
        sb.Append("&numwant=50");

        if (!string.IsNullOrEmpty(opts.Event))
            sb.Append("&event=").Append(opts.Event);
        if (_trackerId != null)
            sb.Append("&trackerid=").Append(HttpUtility.UrlEncode(_trackerId));

        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            var response = await _http.GetByteArrayAsync(sb.ToString(), cts.Token);
            ParseAnnounceResponse(response);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"HTTP tracker error: {ex.Message}");
        }
    }

    private void ParseAnnounceResponse(byte[] data)
    {
        try
        {
            var (dict, _) = BencodeDecoder.DecodeDictionary(data, 0);

            // Failure reason
            if (dict.TryGetValue("failure reason", out var failObj) && failObj is byte[] failBytes)
            {
                OnWarning?.Invoke(Encoding.UTF8.GetString(failBytes));
                return;
            }

            // Warning message
            if (dict.TryGetValue("warning message", out var warnObj) && warnObj is byte[] warnBytes)
                OnWarning?.Invoke(Encoding.UTF8.GetString(warnBytes));

            // Interval
            if (dict.TryGetValue("interval", out var intObj) && intObj is long interval)
            {
                _announceTimer?.Dispose();
                _announceTimer = new Timer(_ => OnAnnounce?.Invoke(), null, (int)(interval * 1000), (int)(interval * 1000));
            }

            // Tracker ID
            if (dict.TryGetValue("tracker id", out var tidObj))
            {
                _trackerId = tidObj is byte[] tidBytes ? Encoding.UTF8.GetString(tidBytes)
                    : tidObj is string tidStr ? tidStr : null;
            }

            // Peers (compact format: 6 bytes per peer — 4 IP + 2 port)
            if (dict.TryGetValue("peers", out var peersObj) && peersObj is byte[] peersBytes)
            {
                for (int i = 0; i + 6 <= peersBytes.Length; i += 6)
                {
                    var ip = $"{peersBytes[i]}.{peersBytes[i + 1]}.{peersBytes[i + 2]}.{peersBytes[i + 3]}";
                    var port = (peersBytes[i + 4] << 8) | peersBytes[i + 5];
                    if (port > 0) OnPeer?.Invoke($"{ip}:{port}");
                }
            }

            // Peers6 (compact IPv6: 18 bytes per peer — 16 IP + 2 port)
            if (dict.TryGetValue("peers6", out var peers6Obj) && peers6Obj is byte[] peers6Bytes)
            {
                for (int i = 0; i + 18 <= peers6Bytes.Length; i += 18)
                {
                    var ipBytes = peers6Bytes[i..(i + 16)];
                    var ip = new System.Net.IPAddress(ipBytes).ToString();
                    var port = (peers6Bytes[i + 16] << 8) | peers6Bytes[i + 17];
                    if (port > 0) OnPeer?.Invoke($"[{ip}]:{port}");
                }
            }

            // Stats
            var complete = dict.TryGetValue("complete", out var cObj) && cObj is long c ? (int)c : 0;
            var incomplete = dict.TryGetValue("incomplete", out var iObj) && iObj is long ic ? (int)ic : 0;
            OnUpdate?.Invoke(new TrackerUpdate { AnnounceUrl = AnnounceUrl, Complete = complete, Incomplete = incomplete });
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"Failed to parse HTTP tracker response: {ex.Message}");
        }
    }

    /// <summary>BEP 48: Scrape — get seeder/leecher counts without announcing.</summary>
    public async Task<ScrapeResult?> ScrapeAsync(CancellationToken ct = default)
    {
        if (Destroyed || ScrapeUrl == null) return null;

        var url = $"{ScrapeUrl}{(ScrapeUrl.Contains('?') ? '&' : '?')}info_hash={UrlEncodeBytes(_infoHash)}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            var response = await _http.GetByteArrayAsync(url, cts.Token);
            return ParseScrapeResponse(response);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"HTTP scrape error: {ex.Message}");
            return null;
        }
    }

    private ScrapeResult? ParseScrapeResponse(byte[] data)
    {
        try
        {
            var (dict, _) = BencodeDecoder.DecodeDictionary(data, 0);
            if (!dict.TryGetValue("files", out var filesObj) || filesObj is not Dictionary<string, object> files)
                return null;

            // The key is the raw 20-byte info hash — iterate all entries
            foreach (var (_, value) in files)
            {
                if (value is not Dictionary<string, object> torrentInfo) continue;
                var complete = torrentInfo.TryGetValue("complete", out var c) && c is long cl ? (int)cl : 0;
                var incomplete = torrentInfo.TryGetValue("incomplete", out var i) && i is long il ? (int)il : 0;
                var downloaded = torrentInfo.TryGetValue("downloaded", out var d) && d is long dl ? (int)dl : 0;
                return new ScrapeResult { Complete = complete, Incomplete = incomplete, Downloaded = downloaded };
            }
        }
        catch { }
        return null;
    }

    /// <summary>URL-encode bytes per RFC 3986 (unreserved: A-Z a-z 0-9 - . _ ~).</summary>
    private static string UrlEncodeBytes(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                (b >= '0' && b <= '9') || b == '-' || b == '.' || b == '_' || b == '~')
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;
        _announceTimer?.Dispose();
    }
}

/// <summary>BEP 48: Result of a tracker scrape request.</summary>
public class ScrapeResult
{
    /// <summary>Number of seeders (complete peers).</summary>
    public int Complete { get; set; }
    /// <summary>Number of leechers (incomplete peers).</summary>
    public int Incomplete { get; set; }
    /// <summary>Total completed downloads.</summary>
    public int Downloaded { get; set; }
}
