using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SpawnDev.WebTorrent;

/// <summary>
/// UDP tracker client (BEP 15). Desktop only — UDP not available in browser.
/// Adapted from the original SpawnDev.WebTorrent implementation for the _Alt architecture.
///
/// Protocol flow:
///   1. Connect: send connection_id request → receive connection_id
///   2. Announce: send announce with info_hash, peer_id → receive peer list
///
/// All messages use network byte order (big-endian).
/// See: http://bittorrent.org/beps/bep_0015.html
/// </summary>
public class UdpTrackerClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _peerId;
    private readonly int _key = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
    private UdpClient? _udp;
    private long _connectionId;
    private DateTime _connectionExpiry;
    private byte[]? _currentInfoHash;
    private int _currentPort;
    private int _announceIntervalSecs = 1800;
    private CancellationTokenSource? _reAnnounceCts;

    public bool IsConnected => _udp != null && _connectionExpiry > DateTime.UtcNow;

    /// <summary>Fires with "ip:port" string for each peer found.</summary>
    public event Action<string>? OnPeer;
    public event Action<int, int>? OnAnnounceResponse;
    public event Action<string>? OnWarning;
    public event Action? OnAnnounce;

    private const int ActionConnect = 0;
    private const int ActionAnnounce = 1;
    private const int ActionError = 3;
    private const long ProtocolId = 0x41727101980;
    private const int MaxConnectRetries = 8;

    public UdpTrackerClient(string trackerUrl, byte[] peerId)
    {
        var uri = new Uri(trackerUrl);
        _host = uri.Host;
        _port = uri.Port > 0 ? uri.Port : 6969;
        _peerId = peerId;
    }

    public async Task StartAsync(byte[] infoHash, int port, CancellationToken ct = default)
    {
        if (OperatingSystem.IsBrowser()) return; // UDP not available in browser

        _currentInfoHash = infoHash;
        _currentPort = port;

        try
        {
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[UDPTracker] Connecting to {_host}:{_port}...");
            _udp = new UdpClient();
            _udp.Connect(_host, _port);

            await ConnectAsync(ct);
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[UDPTracker] Connected to {_host}:{_port}, announcing...");
            await AnnounceAsync(infoHash, port, 0, 0, 0, AnnounceEvent.Started, ct);
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[UDPTracker] Announced to {_host}:{_port}");

            _reAnnounceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = ReannounceLoopAsync(_reAnnounceCts.Token);
        }
        catch (Exception ex)
        {
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[UDPTracker] FAILED {_host}:{_port}: {ex.Message}");
            OnWarning?.Invoke($"UDP tracker error ({_host}:{_port}): {ex.Message}");
        }
    }

    private async Task ReannounceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_announceIntervalSecs), ct);
                if (_currentInfoHash != null && _udp != null)
                    await AnnounceAsync(_currentInfoHash, _currentPort, 0, 0, 0, AnnounceEvent.None, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>BEP 15 §3: exponential backoff. Timeout = 15 * 2^n seconds, n = 0..8.</summary>
    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_udp == null) return;

        for (int attempt = 0; attempt <= MaxConnectRetries; attempt++)
        {
            var transactionId = RandomNumberGenerator.GetInt32(int.MaxValue);
            var timeoutMs = 15000 * (1 << attempt);

            var request = new byte[16];
            WriteInt64BE(request, 0, ProtocolId);
            WriteInt32BE(request, 8, ActionConnect);
            WriteInt32BE(request, 12, transactionId);

            await _udp.SendAsync(request, request.Length);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            try
            {
                var result = await ReceiveWithTimeoutAsync(cts.Token);
                if (result.Length < 16) continue;

                var action = ReadInt32BE(result, 0);
                var respTxId = ReadInt32BE(result, 4);

                if (action == ActionError && result.Length > 8)
                {
                    var errMsg = System.Text.Encoding.UTF8.GetString(result, 8, result.Length - 8);
                    OnWarning?.Invoke($"UDP tracker connect error: {errMsg}");
                    return;
                }

                if (action != ActionConnect || respTxId != transactionId) continue;

                _connectionId = ReadInt64BE(result, 8);
                _connectionExpiry = DateTime.UtcNow.AddMinutes(1);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout for this attempt — retry with longer timeout
            }
        }

        OnWarning?.Invoke($"UDP tracker connect failed after {MaxConnectRetries} retries ({_host}:{_port})");
    }

    public async Task AnnounceAsync(byte[] infoHash, int port,
        long uploaded, long downloaded, long left,
        AnnounceEvent announceEvent = AnnounceEvent.None, CancellationToken ct = default)
    {
        if (_udp == null) return;

        if (_connectionExpiry <= DateTime.UtcNow)
            await ConnectAsync(ct);
        if (_connectionExpiry <= DateTime.UtcNow) return;

        var transactionId = RandomNumberGenerator.GetInt32(int.MaxValue);

        int eventInt = announceEvent switch
        {
            AnnounceEvent.None => 0,
            AnnounceEvent.Completed => 1,
            AnnounceEvent.Started => 2,
            AnnounceEvent.Stopped => 3,
            _ => 0,
        };

        var request = new byte[98];
        WriteInt64BE(request, 0, _connectionId);
        WriteInt32BE(request, 8, ActionAnnounce);
        WriteInt32BE(request, 12, transactionId);
        Array.Copy(infoHash, 0, request, 16, 20);
        Array.Copy(_peerId, 0, request, 36, 20);
        WriteInt64BE(request, 56, downloaded);
        WriteInt64BE(request, 64, left);
        WriteInt64BE(request, 72, uploaded);
        WriteInt32BE(request, 80, eventInt);
        WriteInt32BE(request, 84, 0); // IP address (0 = default)
        WriteInt32BE(request, 88, _key);
        WriteInt32BE(request, 92, -1); // num_want = -1 (default)
        WriteInt16BE(request, 96, (short)port);

        await _udp.SendAsync(request, request.Length);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(15000);

        try
        {
            var result = await ReceiveWithTimeoutAsync(cts.Token);
            if (result.Length < 20) return;

            var action = ReadInt32BE(result, 0);
            var respTxId = ReadInt32BE(result, 4);

            if (action == ActionError)
            {
                var msg = System.Text.Encoding.UTF8.GetString(result, 8, result.Length - 8);
                OnWarning?.Invoke($"UDP tracker error: {msg}");
                return;
            }

            if (action != ActionAnnounce || respTxId != transactionId) return;

            var interval = ReadInt32BE(result, 8);
            if (interval > 0) _announceIntervalSecs = interval;

            var leechers = ReadInt32BE(result, 12);
            var seeders = ReadInt32BE(result, 16);
            OnAnnounceResponse?.Invoke(seeders, leechers);
            OnAnnounce?.Invoke();

            // Extract compact peer list (6 bytes per peer: 4 IP + 2 port)
            for (int i = 20; i + 6 <= result.Length; i += 6)
            {
                var ip = $"{result[i]}.{result[i + 1]}.{result[i + 2]}.{result[i + 3]}";
                var peerPort = (result[i + 4] << 8) | result[i + 5];
                OnPeer?.Invoke($"{ip}:{peerPort}");
            }
        }
        catch (OperationCanceledException)
        {
            OnWarning?.Invoke($"UDP announce timeout ({_host}:{_port})");
        }
    }

    public async Task StopAsync()
    {
        _reAnnounceCts?.Cancel();

        if (_udp != null && _currentInfoHash != null && _connectionExpiry > DateTime.UtcNow)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await AnnounceAsync(_currentInfoHash, _currentPort, 0, 0, 0, AnnounceEvent.Stopped, cts.Token);
            }
            catch { }
        }

        _udp?.Close();
        _udp?.Dispose();
        _udp = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task<byte[]> ReceiveWithTimeoutAsync(CancellationToken ct)
    {
        if (_udp == null) return Array.Empty<byte>();
        var result = await _udp.ReceiveAsync(ct);
        return result.Buffer;
    }

    // Big-endian binary helpers
    public static void WriteInt64BE(byte[] buf, int offset, long value)
    {
        buf[offset] = (byte)(value >> 56); buf[offset + 1] = (byte)(value >> 48);
        buf[offset + 2] = (byte)(value >> 40); buf[offset + 3] = (byte)(value >> 32);
        buf[offset + 4] = (byte)(value >> 24); buf[offset + 5] = (byte)(value >> 16);
        buf[offset + 6] = (byte)(value >> 8); buf[offset + 7] = (byte)value;
    }

    public static void WriteInt32BE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24); buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8); buf[offset + 3] = (byte)value;
    }

    public static void WriteInt16BE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)(value >> 8); buf[offset + 1] = (byte)value;
    }

    public static int ReadInt32BE(byte[] buf, int offset)
        => (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];

    public static long ReadInt64BE(byte[] buf, int offset)
        => ((long)buf[offset] << 56) | ((long)buf[offset + 1] << 48) | ((long)buf[offset + 2] << 40) |
           ((long)buf[offset + 3] << 32) | ((long)buf[offset + 4] << 24) | ((long)buf[offset + 5] << 16) |
           ((long)buf[offset + 6] << 8) | buf[offset + 7];

    public enum AnnounceEvent { None, Completed, Started, Stopped }
}
