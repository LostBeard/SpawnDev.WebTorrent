namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// WebRTC transport for browser-to-browser P2P connections.
/// Uses WebRTC data channels for the BitTorrent wire protocol.
///
/// In Blazor WASM, this wraps RTCPeerConnection via SpawnDev.BlazorJS.
/// On desktop, this could use a native WebRTC library (future).
///
/// Connection flow (via tracker signaling):
///   1. Initiator creates RTCPeerConnection + data channel
///   2. Initiator creates offer SDP → sends to tracker → tracker relays to peer
///   3. Peer receives offer → creates answer SDP → sends to tracker → tracker relays back
///   4. ICE candidates exchange (trickle ICE or bundled in SDP)
///   5. Data channel opens → BitTorrent wire protocol begins
///
/// This class provides the ITransport/IConnection interfaces over WebRTC,
/// keeping the rest of the codebase transport-agnostic.
/// </summary>
public class WebRtcTransport : ITransport
{
    private readonly WebRtcTransportOptions _options;
    private readonly List<WebRtcConnection> _connections = new();

    public string Type => "webrtc";
    public bool CanAccept => true; // can accept incoming offers

    public event Action<IConnection>? OnConnection;

    /// <summary>Fired when an outgoing offer needs to be sent via the tracker.</summary>
    public event Action<string, object>? OnOfferCreated; // toPeerId, offerSdp

    public WebRtcTransport(WebRtcTransportOptions? options = null)
    {
        _options = options ?? new WebRtcTransportOptions();
    }

    public Task StartListeningAsync(int port = 0, CancellationToken ct = default)
    {
        // WebRTC doesn't "listen" — it accepts incoming offers via the tracker
        return Task.CompletedTask;
    }

    /// <summary>
    /// Initiate a connection to a peer by creating a WebRTC offer.
    /// The offer must be sent to the peer via the tracker signaling channel.
    /// Call HandleAnswer() when the peer's answer arrives.
    /// </summary>
    public async Task<IConnection> ConnectAsync(string peerId, CancellationToken ct = default)
    {
        var conn = new WebRtcConnection(peerId, _options);
        _connections.Add(conn);

        // Create offer — the caller must send this via tracker
        var offer = await conn.CreateOfferAsync();
        OnOfferCreated?.Invoke(peerId, offer);

        // Wait for the data channel to open (after answer is received)
        await conn.WaitForOpenAsync(ct);
        OnConnection?.Invoke(conn);
        return conn;
    }

    /// <summary>
    /// Handle an incoming WebRTC offer from a peer (received via tracker).
    /// Creates an answer that must be sent back via the tracker.
    /// </summary>
    public async Task<(IConnection connection, object answer)> HandleOfferAsync(
        string fromPeerId, object offer, CancellationToken ct = default)
    {
        var conn = new WebRtcConnection(fromPeerId, _options);
        _connections.Add(conn);

        var answer = await conn.HandleOfferAsync(offer);
        await conn.WaitForOpenAsync(ct);
        OnConnection?.Invoke(conn);
        return (conn, answer);
    }

    /// <summary>
    /// Handle an incoming WebRTC answer from a peer (for an offer we sent).
    /// </summary>
    public async Task HandleAnswerAsync(string fromPeerId, object answer)
    {
        var conn = _connections.Find(c => c.RemoteId == fromPeerId);
        if (conn != null)
            await conn.HandleAnswerAsync(answer);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var conn in _connections.ToArray())
            await conn.DisposeAsync();
        _connections.Clear();
    }
}

/// <summary>
/// A WebRTC peer connection wrapping RTCPeerConnection + RTCDataChannel.
///
/// In browser (Blazor WASM): uses SpawnDev.BlazorJS wrappers for RTCPeerConnection.
/// On desktop: placeholder — would need a native WebRTC library.
///
/// This is currently a structural placeholder. The actual JS interop implementation
/// will use SpawnDev.BlazorJS.JSObjects.RTCPeerConnection when integrated.
/// </summary>
public class WebRtcConnection : IConnection
{
    private readonly WebRtcTransportOptions _options;
    private readonly TaskCompletionSource _openTcs = new();
    private readonly List<byte> _receiveBuffer = new();
    private readonly SemaphoreSlim _receiveSemaphore = new(0);

    public string RemoteId { get; }
    public string TransportType => "webrtc";
    public bool IsConnected { get; private set; }

    public event Action? OnDataAvailable;
    public event Action? OnDisconnected;

    public WebRtcConnection(string remotePeerId, WebRtcTransportOptions options)
    {
        RemoteId = remotePeerId;
        _options = options;
    }

    /// <summary>Create an SDP offer for initiating a connection.</summary>
    public Task<object> CreateOfferAsync()
    {
        // TODO: In browser, use RTCPeerConnection.createOffer()
        // For now, return a placeholder
        return Task.FromResult<object>(new { type = "offer", sdp = "placeholder" });
    }

    /// <summary>Handle an incoming SDP offer and create an answer.</summary>
    public Task<object> HandleOfferAsync(object offer)
    {
        // TODO: In browser, use RTCPeerConnection.setRemoteDescription() + createAnswer()
        return Task.FromResult<object>(new { type = "answer", sdp = "placeholder" });
    }

    /// <summary>Handle an incoming SDP answer (completes the signaling).</summary>
    public Task HandleAnswerAsync(object answer)
    {
        // TODO: In browser, use RTCPeerConnection.setRemoteDescription()
        IsConnected = true;
        _openTcs.TrySetResult();
        return Task.CompletedTask;
    }

    /// <summary>Wait for the data channel to open.</summary>
    public async Task WaitForOpenAsync(CancellationToken ct = default)
    {
        using var reg = ct.Register(() => _openTcs.TrySetCanceled());
        await _openTcs.Task;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        // TODO: In browser, use RTCDataChannel.send()
        return Task.CompletedTask;
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // TODO: In browser, data arrives via RTCDataChannel.onmessage event
        await _receiveSemaphore.WaitAsync(ct);
        lock (_receiveBuffer)
        {
            int count = Math.Min(buffer.Length, _receiveBuffer.Count);
            _receiveBuffer.CopyTo(0, buffer.Span.ToArray(), 0, count);
            _receiveBuffer.RemoveRange(0, count);
            return count;
        }
    }

    /// <summary>Called when data arrives on the data channel (from JS event).</summary>
    public void OnDataReceived(byte[] data)
    {
        lock (_receiveBuffer)
        {
            _receiveBuffer.AddRange(data);
        }
        _receiveSemaphore.Release();
        OnDataAvailable?.Invoke();
    }

    public Task CloseAsync()
    {
        IsConnected = false;
        OnDisconnected?.Invoke();
        // TODO: Close RTCPeerConnection and RTCDataChannel
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _receiveSemaphore.Dispose();
    }
}

/// <summary>WebRTC transport configuration.</summary>
public class WebRtcTransportOptions
{
    /// <summary>ICE servers for NAT traversal.</summary>
    public string[] IceServers { get; set; } = new[]
    {
        "stun:stun.l.google.com:19302",
        "stun:global.stun.twilio.com:3478",
    };

    /// <summary>Data channel label.</summary>
    public string ChannelLabel { get; set; } = "spawndev-webtorrent";

    /// <summary>Whether to use ordered delivery (slower but reliable).</summary>
    public bool Ordered { get; set; } = false;

    /// <summary>Max retransmits (0 = unreliable, like UDP).</summary>
    public int? MaxRetransmits { get; set; } = null;
}
