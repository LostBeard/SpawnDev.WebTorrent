using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SpawnDev.WebTorrent.Discovery;

/// <summary>
/// UDP tracker client (BEP 15). Desktop only — UDP not available in browser.
/// Most public BitTorrent trackers use UDP (udp://tracker.opentrackr.org:1337, etc.).
///
/// Protocol flow:
///   1. Connect: send connection_id request → receive connection_id
///   2. Announce: send announce with info_hash, peer_id → receive peer list
///   3. Scrape (optional): get seeder/leecher counts
///
/// All messages use network byte order (big-endian).
/// See: http://bittorrent.org/beps/bep_0015.html
/// </summary>
public class UdpTrackerClient : IDiscovery
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _peerId;
    private UdpClient? _udp;
    private long _connectionId;
    private DateTime _connectionExpiry;
    private byte[]? _currentInfoHash;

    public string Type => "udp-tracker";
    public bool IsConnected => _udp != null && _connectionExpiry > DateTime.UtcNow;

    public event Action<PeerInfo>? OnPeer;
    public event Action<int, int>? OnAnnounceResponse; // seeders, leechers
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    // Actions
    private const int ActionConnect = 0;
    private const int ActionAnnounce = 1;
    private const int ActionScrape = 2;
    private const int ActionError = 3;

    // Magic connection ID for connect request
    private const long ProtocolId = 0x41727101980;

    public UdpTrackerClient(string trackerUrl, byte[] peerId)
    {
        // Parse udp://host:port/announce
        var uri = new Uri(trackerUrl);
        _host = uri.Host;
        _port = uri.Port > 0 ? uri.Port : 6969;
        _peerId = peerId;
    }

    public async Task StartAsync(byte[] infoHash, int port, CancellationToken ct = default)
    {
        _currentInfoHash = infoHash;

        try
        {
            _udp = new UdpClient();
            _udp.Connect(_host, _port);

            // Step 1: Connect
            await ConnectAsync(ct);

            // Step 2: Announce
            await AnnounceAsync(infoHash, port, 0, 0, 0, ct);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"UDP tracker error: {ex.Message}");
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_udp == null) return;

        var transactionId = RandomNumberGenerator.GetInt32(int.MaxValue);

        // Connect request: 8 bytes protocol_id + 4 bytes action(0) + 4 bytes transaction_id
        var request = new byte[16];
        WriteInt64BE(request, 0, ProtocolId);
        WriteInt32BE(request, 8, ActionConnect);
        WriteInt32BE(request, 12, transactionId);

        await _udp.SendAsync(request, request.Length);

        // Receive response with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(5000);

        try
        {
            var result = await ReceiveWithTimeoutAsync(cts.Token);
            if (result.Length < 16) return;

            var action = ReadInt32BE(result, 0);
            var respTxId = ReadInt32BE(result, 4);

            if (action != ActionConnect || respTxId != transactionId)
            {
                OnError?.Invoke("Invalid connect response");
                return;
            }

            _connectionId = ReadInt64BE(result, 8);
            _connectionExpiry = DateTime.UtcNow.AddMinutes(1); // connection_id valid for 1 minute
            OnConnected?.Invoke();
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("UDP connect timeout");
        }
    }

    public async Task AnnounceAsync(byte[] infoHash, int port,
        long uploaded, long downloaded, long left, CancellationToken ct = default)
    {
        if (_udp == null) return;

        // Re-connect if connection expired
        if (_connectionExpiry <= DateTime.UtcNow)
            await ConnectAsync(ct);

        if (_connectionExpiry <= DateTime.UtcNow) return; // connect failed

        var transactionId = RandomNumberGenerator.GetInt32(int.MaxValue);

        // Announce request: 98 bytes
        var request = new byte[98];
        WriteInt64BE(request, 0, _connectionId);           // connection_id
        WriteInt32BE(request, 8, ActionAnnounce);           // action = announce
        WriteInt32BE(request, 12, transactionId);           // transaction_id
        Array.Copy(infoHash, 0, request, 16, 20);          // info_hash
        Array.Copy(_peerId, 0, request, 36, 20);            // peer_id
        WriteInt64BE(request, 56, downloaded);               // downloaded
        WriteInt64BE(request, 64, left);                     // left
        WriteInt64BE(request, 72, uploaded);                  // uploaded
        WriteInt32BE(request, 80, 0);                        // event: 0=none
        WriteInt32BE(request, 84, 0);                        // IP address: 0=default
        WriteInt32BE(request, 88, RandomNumberGenerator.GetInt32(int.MaxValue)); // key (random)
        WriteInt32BE(request, 92, -1);                       // num_want: -1=default
        WriteInt16BE(request, 96, (short)port);              // port

        await _udp.SendAsync(request, request.Length);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(5000);

        try
        {
            var result = await ReceiveWithTimeoutAsync(cts.Token);
            if (result.Length < 20) return;

            var action = ReadInt32BE(result, 0);
            var respTxId = ReadInt32BE(result, 4);

            if (action == ActionError)
            {
                var msg = System.Text.Encoding.UTF8.GetString(result, 8, result.Length - 8);
                OnError?.Invoke($"Tracker error: {msg}");
                return;
            }

            if (action != ActionAnnounce || respTxId != transactionId) return;

            // Parse announce response
            // interval(4) + leechers(4) + seeders(4) + peers(6*N)
            var leechers = ReadInt32BE(result, 12);
            var seeders = ReadInt32BE(result, 16);
            OnAnnounceResponse?.Invoke(seeders, leechers);

            // Parse compact peer list (6 bytes each: 4 IP + 2 port)
            for (int i = 20; i + 6 <= result.Length; i += 6)
            {
                var ip = $"{result[i]}.{result[i + 1]}.{result[i + 2]}.{result[i + 3]}";
                var peerPort = (result[i + 4] << 8) | result[i + 5];

                OnPeer?.Invoke(new PeerInfo
                {
                    Address = $"{ip}:{peerPort}",
                    Source = "udp-tracker",
                });
            }
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("UDP announce timeout");
        }
    }

    private async Task<byte[]> ReceiveWithTimeoutAsync(CancellationToken ct)
    {
        if (_udp == null) return Array.Empty<byte>();

        // UdpClient.ReceiveAsync with cancellation
        var receiveTask = _udp.ReceiveAsync(ct);
        var result = await receiveTask;
        return result.Buffer;
    }

    public async Task StopAsync()
    {
        _udp?.Close();
        _udp?.Dispose();
        _udp = null;
        OnDisconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    // Big-endian helpers
    private static void WriteInt64BE(byte[] buf, int offset, long value)
    {
        buf[offset] = (byte)(value >> 56);
        buf[offset + 1] = (byte)(value >> 48);
        buf[offset + 2] = (byte)(value >> 40);
        buf[offset + 3] = (byte)(value >> 32);
        buf[offset + 4] = (byte)(value >> 24);
        buf[offset + 5] = (byte)(value >> 16);
        buf[offset + 6] = (byte)(value >> 8);
        buf[offset + 7] = (byte)value;
    }

    private static void WriteInt32BE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteInt16BE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static int ReadInt32BE(byte[] buf, int offset)
        => (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];

    private static long ReadInt64BE(byte[] buf, int offset)
        => ((long)buf[offset] << 56) | ((long)buf[offset + 1] << 48) | ((long)buf[offset + 2] << 40) |
           ((long)buf[offset + 3] << 32) | ((long)buf[offset + 4] << 24) | ((long)buf[offset + 5] << 16) |
           ((long)buf[offset + 6] << 8) | buf[offset + 7];
}
