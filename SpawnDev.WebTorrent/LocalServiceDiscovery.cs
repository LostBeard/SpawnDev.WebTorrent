using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SpawnDev.WebTorrent;

/// <summary>
/// BEP 14: Local Service Discovery (LSD).
/// Desktop only — broadcasts BT-SEARCH messages on the LAN via UDP multicast.
/// Discovers peers on the local network without requiring a tracker.
/// Multicast group: 239.192.152.143:6771 (IPv4)
/// </summary>
public class LocalServiceDiscovery : IAsyncDisposable
{
    private const string MulticastGroup = "239.192.152.143";
    private const int MulticastPort = 6771;
    private const int AnnounceIntervalMs = 300_000; // 5 minutes per BEP 14

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly byte[] _infoHash;
    private readonly int _port;

    /// <summary>Fires with "ip:port" when a local peer announces itself.</summary>
    public event Action<string>? OnPeer;
    public event Action<string>? OnWarning;

    public LocalServiceDiscovery(byte[] infoHash, int port = 6881)
    {
        _infoHash = infoHash;
        _port = port;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsBrowser()) return;

        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
            _udp.JoinMulticastGroup(IPAddress.Parse(MulticastGroup));

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Start listening for announcements
            _ = ReceiveLoopAsync(_cts.Token);

            // Announce ourselves
            await AnnounceAsync(ct);

            // Re-announce periodically
            _ = AnnounceLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"LSD start failed: {ex.Message}");
        }
    }

    /// <summary>Send a BT-SEARCH announce to the multicast group.</summary>
    public async Task AnnounceAsync(CancellationToken ct = default)
    {
        if (_udp == null) return;

        var infoHashHex = Convert.ToHexString(_infoHash).ToLowerInvariant();
        var message = $"BT-SEARCH * HTTP/1.1\r\nHost: {MulticastGroup}:{MulticastPort}\r\nPort: {_port}\r\nInfohash: {infoHashHex}\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(message);

        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(MulticastGroup), MulticastPort);
            await _udp.SendAsync(bytes, bytes.Length, ep);
        }
        catch { }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(AnnounceIntervalMs, ct);
                await AnnounceAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _udp != null)
            {
                var result = await _udp.ReceiveAsync(ct);
                ParseMessage(result.Buffer, result.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void ParseMessage(byte[] data, IPEndPoint from)
    {
        try
        {
            var msg = Encoding.ASCII.GetString(data);
            if (!msg.StartsWith("BT-SEARCH")) return;

            int? port = null;
            string? infoHash = null;

            foreach (var line in msg.Split("\r\n"))
            {
                if (line.StartsWith("Port:", StringComparison.OrdinalIgnoreCase))
                    port = int.Parse(line["Port:".Length..].Trim());
                else if (line.StartsWith("Infohash:", StringComparison.OrdinalIgnoreCase))
                    infoHash = line["Infohash:".Length..].Trim().ToLowerInvariant();
            }

            // Only respond if info hash matches ours
            var ourHash = Convert.ToHexString(_infoHash).ToLowerInvariant();
            if (infoHash == ourHash && port.HasValue)
            {
                OnPeer?.Invoke($"{from.Address}:{port.Value}");
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _udp?.DropMulticastGroup(IPAddress.Parse(MulticastGroup)); } catch { }
        _udp?.Close();
        _udp?.Dispose();
        _udp = null;
    }
}
