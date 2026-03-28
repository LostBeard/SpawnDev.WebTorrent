using System.Net;
using System.Net.Sockets;

namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// TCP transport for desktop .NET. Handles both incoming and outgoing
/// TCP connections for the BitTorrent peer wire protocol.
/// Not available in Blazor WASM — browser peers use WebRTC.
/// </summary>
public class TcpTransport : ITransport
{
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;

    public string Type => "tcp";
    public bool CanAccept => true;

    public event Action<IConnection>? OnConnection;

    public async Task StartListeningAsync(int port = 0, CancellationToken ct = default)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _listenerCts = new CancellationTokenSource();

        _ = AcceptLoopAsync(_listenerCts.Token);
        await Task.CompletedTask;
    }

    public async Task<IConnection> ConnectAsync(string address, CancellationToken ct = default)
    {
        // address format: "ip:port"
        var parts = address.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
            throw new ArgumentException($"Invalid TCP address: {address}. Expected ip:port");

        var client = new TcpClient();
        await client.ConnectAsync(parts[0], port, ct);
        var conn = new TcpConnection(client);
        return conn;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                var conn = new TcpConnection(client);
                OnConnection?.Invoke(conn);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listenerCts?.Cancel();
        _listener?.Stop();
        await Task.CompletedTask;
    }
}

/// <summary>
/// TCP connection wrapper implementing IConnection.
/// Provides the bidirectional byte stream for the wire protocol.
/// </summary>
public class TcpConnection : IConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public string RemoteId { get; }
    public string TransportType => "tcp";
    public bool IsConnected => _client.Connected;

#pragma warning disable CS0067 // TCP uses stream-based receive, not event-driven
    public event Action? OnDataAvailable;
#pragma warning restore CS0067
    public event Action? OnDisconnected;

    public TcpConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        RemoteId = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            int read = await _stream.ReadAsync(buffer, ct);
            if (read == 0) OnDisconnected?.Invoke();
            return read;
        }
        catch
        {
            OnDisconnected?.Invoke();
            return 0;
        }
    }

    public async Task CloseAsync()
    {
        _stream.Close();
        _client.Close();
        OnDisconnected?.Invoke();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _stream.Dispose();
        _client.Dispose();
    }
}
