namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// Abstraction over peer-to-peer transport. Implementations:
/// - TcpTransport (desktop): standard TCP sockets
/// - WebRtcTransport (browser): WebRTC data channels
/// - WebSocketTransport (both): WebSocket connections to trackers
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Transport type identifier.</summary>
    string Type { get; }

    /// <summary>Whether this transport can accept incoming connections.</summary>
    bool CanAccept { get; }

    /// <summary>Start listening for incoming connections (if supported).</summary>
    Task StartListeningAsync(int port = 0, CancellationToken ct = default);

    /// <summary>Connect to a remote peer.</summary>
    Task<IConnection> ConnectAsync(string address, CancellationToken ct = default);

    /// <summary>Fired when an incoming connection is received.</summary>
    event Action<IConnection> OnConnection;
}

/// <summary>
/// A single peer connection over any transport.
/// Provides a bidirectional byte stream for the BitTorrent wire protocol.
/// </summary>
public interface IConnection : IAsyncDisposable
{
    /// <summary>Remote peer identifier (IP:port for TCP, peer ID for WebRTC).</summary>
    string RemoteId { get; }

    /// <summary>Transport type this connection uses.</summary>
    string TransportType { get; }

    /// <summary>Whether the connection is still open.</summary>
    bool IsConnected { get; }

    /// <summary>Send raw bytes to the peer.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Receive raw bytes from the peer.</summary>
    Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>Close the connection.</summary>
    Task CloseAsync();

    /// <summary>Fired when data is available to read.</summary>
    event Action OnDataAvailable;

    /// <summary>Fired when the connection is closed.</summary>
    event Action OnDisconnected;
}
