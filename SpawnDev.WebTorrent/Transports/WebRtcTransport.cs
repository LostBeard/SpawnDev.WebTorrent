using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.JSObjects.WebRTC;
using System.Text.Json;

namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// WebRTC transport for browser-to-browser P2P connections.
/// Uses SpawnDev.BlazorJS RTCPeerConnection + RTCDataChannel for the BitTorrent wire protocol.
///
/// Connection flow (via tracker signaling):
///   1. Initiator creates RTCPeerConnection + data channel
///   2. Initiator creates offer SDP, waits for ICE gathering, fires OnOfferCreated
///   3. PeerCoordinator relays offer through tracker to remote peer
///   4. Remote peer calls HandleOfferAsync, creates answer, returns it
///   5. PeerCoordinator relays answer back through tracker
///   6. Original peer calls HandleAnswerAsync, sets remote description
///   7. Data channel opens on both sides -> BitTorrent wire protocol begins
/// </summary>
public class WebRtcTransport : ITransport
{
    private readonly WebRtcTransportOptions _options;
    private readonly List<WebRtcConnection> _connections = new();

    public string Type => "webrtc";
    public bool CanAccept => true;

    public event Action<IConnection>? OnConnection;

    /// <summary>Fired when an outgoing offer needs to be sent via the tracker.</summary>
    public event Action<string, object>? OnOfferCreated;

    public WebRtcTransport(WebRtcTransportOptions? options = null)
    {
        _options = options ?? new WebRtcTransportOptions();
    }

    public Task StartListeningAsync(int port = 0, CancellationToken ct = default)
    {
        // WebRTC doesn't "listen" - it accepts incoming offers via the tracker
        return Task.CompletedTask;
    }

    /// <summary>
    /// Initiate a connection to a peer by creating a WebRTC offer.
    /// The offer is fired via OnOfferCreated for the PeerCoordinator to relay through the tracker.
    /// Call HandleAnswerAsync() when the peer's answer arrives.
    /// </summary>
    public async Task<IConnection> ConnectAsync(string peerId, CancellationToken ct = default)
    {
        var conn = new WebRtcConnection(peerId, _options);
        _connections.Add(conn);

        var offer = await conn.CreateOfferAsync();
        OnOfferCreated?.Invoke(peerId, offer);

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
/// A single WebRTC peer connection wrapping RTCPeerConnection + RTCDataChannel
/// via SpawnDev.BlazorJS browser interop.
///
/// Handles the full lifecycle: SDP offer/answer, ICE gathering, data channel setup,
/// and bidirectional binary data transfer for the BitTorrent wire protocol.
/// </summary>
public class WebRtcConnection : IConnection
{
    private readonly WebRtcTransportOptions _options;
    private readonly TaskCompletionSource _openTcs = new();
    private readonly List<byte> _receiveBuffer = new();
    private readonly SemaphoreSlim _receiveSemaphore = new(0);
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dc;

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

    private RTCPeerConnection CreatePeerConnection()
    {
        var config = new RTCConfiguration
        {
            IceServers = _options.IceServers.Select(url => new RTCIceServer
            {
                Urls = url
            }).ToArray()
        };
        var pc = new RTCPeerConnection(config);

        pc.OnConnectionStateChange += OnPeerConnectionStateChange;

        return pc;
    }

    private void OnPeerConnectionStateChange(Event e)
    {
        if (_pc == null) return;
        var state = _pc.ConnectionState;
        if (state == "disconnected" || state == "failed" || state == "closed")
        {
            IsConnected = false;
            OnDisconnected?.Invoke();
        }
    }

    private void SetupDataChannel(RTCDataChannel dc)
    {
        _dc = dc;
        _dc.BinaryType = "arraybuffer";

        _dc.OnOpen += OnDataChannelOpen;
        _dc.OnClose += OnDataChannelClose;
        _dc.OnMessage += OnDataChannelMessage;
    }

    private void OnDataChannelOpen(RTCDataChannelEvent e)
    {
        IsConnected = true;
        _openTcs.TrySetResult();
    }

    private void OnDataChannelClose(Event e)
    {
        IsConnected = false;
        OnDisconnected?.Invoke();
    }

    private void OnDataChannelMessage(MessageEvent e)
    {
        byte[]? bytes = null;
        var dataType = e.TypeOfData;

        if (dataType == "ArrayBuffer")
        {
            using var ab = e.GetData<ArrayBuffer>();
            using var uint8 = new Uint8Array(ab);
            bytes = uint8.ReadBytes();
        }
        else if (dataType == "String")
        {
            var text = e.GetData<string>();
            bytes = System.Text.Encoding.UTF8.GetBytes(text);
        }

        if (bytes != null && bytes.Length > 0)
        {
            lock (_receiveBuffer)
            {
                _receiveBuffer.AddRange(bytes);
            }
            _receiveSemaphore.Release();
            OnDataAvailable?.Invoke();
        }
    }

    /// <summary>Create SDP offer (initiator side).</summary>
    public async Task<object> CreateOfferAsync()
    {
        _pc = CreatePeerConnection();

        // Create data channel BEFORE creating offer (required by WebRTC spec)
        var dcOptions = new RTCDataChannelOptions
        {
            Ordered = _options.Ordered,
        };
        if (_options.MaxRetransmits.HasValue)
            dcOptions.MaxRetransmits = (ushort)_options.MaxRetransmits.Value;

        var dc = _pc.CreateDataChannel(_options.ChannelLabel, dcOptions);
        SetupDataChannel(dc);

        // Create offer and set as local description
        var offer = await _pc.CreateOffer();
        await _pc.SetLocalDescription(offer);

        // Wait for ICE gathering to complete so all candidates are embedded in the SDP
        await WaitForIceGatheringAsync();

        // Return the complete local description (includes ICE candidates)
        return _pc.LocalDescription!;
    }

    /// <summary>Handle incoming SDP offer (responder side) and create answer.</summary>
    public async Task<object> HandleOfferAsync(object offer)
    {
        _pc = CreatePeerConnection();

        // Listen for incoming data channel from the initiator
        _pc.OnDataChannel += OnRemoteDataChannel;

        // Deserialize and set the remote offer
        var offerDesc = DeserializeDescription(offer);
        await _pc.SetRemoteDescription(offerDesc);

        // Create answer and set as local description
        var answer = await _pc.CreateAnswer();
        await _pc.SetLocalDescription(answer);

        // Wait for ICE gathering to complete
        await WaitForIceGatheringAsync();

        // Return the complete local description (includes ICE candidates)
        return _pc.LocalDescription!;
    }

    private void OnRemoteDataChannel(RTCDataChannelEvent e)
    {
        SetupDataChannel(e.Channel);
    }

    /// <summary>Handle incoming SDP answer (initiator side, completes signaling).</summary>
    public async Task HandleAnswerAsync(object answer)
    {
        if (_pc == null) return;
        var answerDesc = DeserializeDescription(answer);
        await _pc.SetRemoteDescription(answerDesc);
        // After this, ICE connectivity checks run and the data channel opens
    }

    /// <summary>Deserialize an SDP description from various input formats.</summary>
    private static RTCSessionDescription DeserializeDescription(object desc)
    {
        if (desc is RTCSessionDescription rtcDesc)
            return rtcDesc;

        if (desc is JsonElement json)
        {
            var type = json.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
            var sdp = json.TryGetProperty("sdp", out var sdpProp) ? sdpProp.GetString() ?? "" : "";
            return new RTCSessionDescription { Type = type, Sdp = sdp };
        }

        // Fallback: JSON round-trip for unknown object types
        var jsonStr = JsonSerializer.Serialize(desc);
        return JsonSerializer.Deserialize<RTCSessionDescription>(jsonStr)
            ?? new RTCSessionDescription();
    }

    /// <summary>Wait for ICE gathering to complete with a timeout.</summary>
    private async Task WaitForIceGatheringAsync(int timeoutMs = 10000)
    {
        if (_pc == null) return;
        if (_pc.IceGatheringState == "complete") return;

        var tcs = new TaskCompletionSource();

        void handler(Event e)
        {
            if (_pc?.IceGatheringState == "complete")
                tcs.TrySetResult();
        }

        _pc.OnIceGatheringStateChange += handler;

        // Re-check after subscribing to avoid race condition
        if (_pc.IceGatheringState == "complete")
            tcs.TrySetResult();

        // Wait with timeout - ICE gathering should be fast with STUN servers
        using var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetResult()); // proceed even on timeout

        try
        {
            await tcs.Task;
        }
        finally
        {
            _pc.OnIceGatheringStateChange -= handler;
        }
    }

    /// <summary>Wait for the data channel to open.</summary>
    public async Task WaitForOpenAsync(CancellationToken ct = default)
    {
        using var reg = ct.Register(() => _openTcs.TrySetCanceled());
        await _openTcs.Task;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_dc == null || _dc.ReadyState != "open")
            throw new InvalidOperationException("Data channel is not open");

        _dc.Send(data.ToArray());
        return Task.CompletedTask;
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await _receiveSemaphore.WaitAsync(ct);
        lock (_receiveBuffer)
        {
            int count = Math.Min(buffer.Length, _receiveBuffer.Count);
            for (int i = 0; i < count; i++)
                buffer.Span[i] = _receiveBuffer[i];
            _receiveBuffer.RemoveRange(0, count);
            return count;
        }
    }

    public Task CloseAsync()
    {
        IsConnected = false;
        try { _dc?.Close(); } catch { }
        try { _pc?.Close(); } catch { }
        OnDisconnected?.Invoke();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _dc?.Dispose();
        _pc?.Dispose();
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

    /// <summary>Max retransmits (null = browser default).</summary>
    public int? MaxRetransmits { get; set; } = null;
}
