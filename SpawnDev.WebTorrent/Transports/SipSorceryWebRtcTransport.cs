using SIPSorcery.Net;
using System.Text;
using System.Text.Json;

namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// Desktop WebRTC transport using SIPSorcery for native .NET peer connections.
/// Mirrors WebRtcTransport (browser) — same ITransport/IConnection interfaces,
/// same signaling protocol, same tracker. Desktop and browser peers interop seamlessly.
///
/// Uses SIPSorcery's RTCPeerConnection + RTCDataChannel for data exchange.
/// Inspired by SpawnDev.RTLink's RPCWebRTCSIPConnection pattern.
/// </summary>
public class SipSorceryWebRtcTransport : IWebRtcTransport
{
    private readonly WebRtcTransportOptions _options;
    private readonly List<SipSorceryWebRtcConnection> _connections = new();

    public string Type => "webrtc";
    public bool CanAccept => true;

    public event Action<IConnection>? OnConnection;

    /// <summary>Fired when an outgoing offer needs to be sent via the tracker.</summary>
    public event Action<string, object>? OnOfferCreated;

    public SipSorceryWebRtcTransport(WebRtcTransportOptions? options = null)
    {
        _options = options ?? new WebRtcTransportOptions();
    }

    public Task StartListeningAsync(int port = 0, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public async Task<IConnection> ConnectAsync(string peerId, CancellationToken ct = default)
    {
        var conn = new SipSorceryWebRtcConnection(peerId, _options);
        _connections.Add(conn);

        var offer = await conn.CreateOfferAsync();
        OnOfferCreated?.Invoke(peerId, offer);

        await conn.WaitForOpenAsync(ct);
        OnConnection?.Invoke(conn);
        return conn;
    }

    public async Task<(IConnection connection, object answer)> HandleOfferAsync(
        string fromPeerId, object offer, CancellationToken ct = default)
    {
        var conn = new SipSorceryWebRtcConnection(fromPeerId, _options);
        _connections.Add(conn);

        // Create answer but DON'T wait for data channel — the answer must be sent
        // back to the initiator first, then ICE completes, then data channel opens.
        // Waiting here deadlocks (same fix as WebRtcTransport.HandleOfferAsync).
        var answer = await conn.HandleOfferAsync(offer);
        return (conn, answer);
    }

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
/// A single desktop WebRTC peer connection using SIPSorcery.
/// Handles offer/answer signaling, ICE, and binary data channel communication.
/// </summary>
public class SipSorceryWebRtcConnection : IConnection
{
    private readonly WebRtcTransportOptions _options;
    private readonly TaskCompletionSource _openTcs = new();
    private readonly List<byte> _receiveBuffer = new();
    private TaskCompletionSource? _receiveSignal;
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dc;

    public string RemoteId { get; }
    public string TransportType => "webrtc";
    public bool IsConnected { get; private set; }

    public event Action? OnDataAvailable;
    public event Action? OnDisconnected;

    public SipSorceryWebRtcConnection(string remotePeerId, WebRtcTransportOptions options)
    {
        RemoteId = remotePeerId;
        _options = options;
    }

    private RTCPeerConnection CreatePeerConnection()
    {
        var config = new RTCConfiguration();
        config.iceServers = new List<RTCIceServer>();
        foreach (var url in _options.IceServers)
        {
            config.iceServers.Add(new RTCIceServer { urls = url });
        }

        var pc = new RTCPeerConnection(config);

        pc.onconnectionstatechange += (state) =>
        {
            if (state == RTCPeerConnectionState.disconnected ||
                state == RTCPeerConnectionState.failed ||
                state == RTCPeerConnectionState.closed)
            {
                IsConnected = false;
                OnDisconnected?.Invoke();
            }
        };

        return pc;
    }

    private void SetupDataChannel(RTCDataChannel dc)
    {
        _dc = dc;

        dc.onopen += () =>
        {
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[SipSorcery] DC onopen: label={dc.label}");
            IsConnected = true;
            _openTcs.TrySetResult();
        };

        // If the data channel is already open when we subscribe (SipSorcery can fire
        // ondatachannel with state=open), trigger immediately
        if (dc.readyState == RTCDataChannelState.open)
        {
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[SipSorcery] DC already open: label={dc.label}");
            IsConnected = true;
            _openTcs.TrySetResult();
        }

        dc.onclose += () =>
        {
            IsConnected = false;
            var signal = _receiveSignal;
            _receiveSignal = null;
            signal?.TrySetCanceled();
            OnDisconnected?.Invoke();
        };

        dc.onmessage += (RTCDataChannel channel, DataChannelPayloadProtocols protocol, byte[] data) =>
        {
            if (data.Length > 0)
            {
                lock (_receiveBuffer)
                {
                    _receiveBuffer.AddRange(data);
                }
                var signal = _receiveSignal;
                _receiveSignal = null;
                signal?.TrySetResult();
                OnDataAvailable?.Invoke();
            }
        };
    }

    /// <summary>Create SDP offer (initiator side).</summary>
    public async Task<object> CreateOfferAsync()
    {
        _pc = CreatePeerConnection();

        // Create data channel before offer (same as browser)
        var dc = await _pc.createDataChannel(_options.ChannelLabel, new RTCDataChannelInit
        {
            ordered = _options.Ordered,
            maxRetransmits = _options.MaxRetransmits.HasValue ? (ushort?)_options.MaxRetransmits.Value : null,
        });
        SetupDataChannel(dc);

        // Create offer
        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);

        // Wait for ICE gathering to complete
        await WaitForIceGatheringAsync();

        var localDesc = _pc.localDescription;
        return new { type = localDesc.type.ToString(), sdp = localDesc.sdp.ToString() };
    }

    /// <summary>Handle incoming SDP offer (responder side) and create answer.</summary>
    public async Task<object> HandleOfferAsync(object offer)
    {
        _pc = CreatePeerConnection();

        // Listen for incoming data channel
        _pc.ondatachannel += (dc) =>
        {
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[SipSorcery] ondatachannel: label={dc.label} state={dc.readyState}");
            SetupDataChannel(dc);
        };

        // Set remote offer
        var offerDesc = DeserializeDescription(offer);
        _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerDesc.Sdp,
        });

        // Create answer
        var answer = _pc.createAnswer();
        await _pc.setLocalDescription(answer);

        // Wait for ICE gathering
        await WaitForIceGatheringAsync();

        var localDesc = _pc.localDescription;
        return new { type = localDesc.type.ToString(), sdp = localDesc.sdp.ToString() };
    }

    /// <summary>Handle incoming SDP answer (initiator side, completes signaling).</summary>
    public Task HandleAnswerAsync(object answer)
    {
        if (_pc == null) return Task.CompletedTask;
        var answerDesc = DeserializeDescription(answer);
        _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answerDesc.Sdp,
        });
        return Task.CompletedTask;
    }

    private static DescriptionDto DeserializeDescription(object desc)
    {
        if (desc is JsonElement json)
        {
            var type = json.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var sdp = json.TryGetProperty("sdp", out var s) ? s.GetString() ?? "" : "";
            return new DescriptionDto { Type = type, Sdp = sdp };
        }
        var jsonStr = JsonSerializer.Serialize(desc);
        return JsonSerializer.Deserialize<DescriptionDto>(jsonStr) ?? new DescriptionDto();
    }

    private async Task WaitForIceGatheringAsync(int timeoutMs = 10000)
    {
        if (_pc == null) return;
        if (_pc.iceGatheringState == RTCIceGatheringState.complete) return;

        var tcs = new TaskCompletionSource();

        _pc.onicegatheringstatechange += (state) =>
        {
            if (state == RTCIceGatheringState.complete)
                tcs.TrySetResult();
        };

        if (_pc.iceGatheringState == RTCIceGatheringState.complete)
            tcs.TrySetResult();

        using var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetResult());

        await tcs.Task;
    }

    public async Task WaitForOpenAsync(CancellationToken ct = default)
    {
        using var reg = ct.Register(() => _openTcs.TrySetCanceled());
        await _openTcs.Task;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_dc == null || _dc.readyState != RTCDataChannelState.open)
            throw new InvalidOperationException("Data channel is not open");
        _dc.send(data.ToArray());
        return Task.CompletedTask;
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            lock (_receiveBuffer)
            {
                if (_receiveBuffer.Count > 0)
                {
                    int count = Math.Min(buffer.Length, _receiveBuffer.Count);
                    for (int i = 0; i < count; i++)
                        buffer.Span[i] = _receiveBuffer[i];
                    _receiveBuffer.RemoveRange(0, count);
                    return count;
                }
            }

            // Buffer empty — wait for new data from the data channel
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _receiveSignal = tcs;

            // Re-check buffer after creating signal to avoid race
            lock (_receiveBuffer)
            {
                if (_receiveBuffer.Count > 0)
                {
                    _receiveSignal = null;
                    continue;
                }
            }

            using var reg = ct.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
    }

    public Task CloseAsync()
    {
        IsConnected = false;
        var signal = _receiveSignal;
        _receiveSignal = null;
        signal?.TrySetCanceled();
        try { _dc?.close(); } catch { }
        try { _pc?.close(); } catch { }
        OnDisconnected?.Invoke();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _pc?.Dispose();
    }

    private class DescriptionDto
    {
        public string Type { get; set; } = "";
        public string Sdp { get; set; } = "";
    }
}
