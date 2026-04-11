using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Browser WebRTC peer connection via SpawnDev.BlazorJS RTCPeerConnection.
/// Uses the W3C WebRTC API as implemented by SpawnDev.BlazorJS.
/// Patterns taken from the working WebRtcTransport.cs in the original project.
/// </summary>
public class BrowserPeer : SimplePeer
{
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dc;
    private bool _iceComplete;
    private TaskCompletionSource? _iceCompleteTcs;
    private readonly TaskCompletionSource _openTcs = new();
    private Timer? _closingTimer;
    private readonly string[] _iceServers;

    public BrowserPeer(bool initiator, string[]? iceServers = null, string? channelName = null, bool trickle = false)
        : base(initiator, channelName, trickle)
    {
        _iceServers = iceServers ?? DefaultIceServers;
    }

    public override async Task InitAsync()
    {
        var config = new RTCConfiguration
        {
            IceServers = _iceServers.Select(url => new RTCIceServer { Urls = url }).ToArray()
        };

        _pc = new RTCPeerConnection(config);

        // W3C event: connectionstatechange
        _pc.OnConnectionStateChange += (Event e) =>
        {
            var state = _pc?.ConnectionState;
            if (state == "failed" || state == "closed" || state == "disconnected")
                EmitDisconnect();
        };

        // W3C event: icecandidate
        _pc.OnIceCandidate += (RTCPeerConnectionEvent ev) =>
        {
            var candidate = ev.Candidate;
            if (candidate == null || string.IsNullOrEmpty(candidate.Candidate))
            {
                // Null/empty candidate = ICE gathering complete
                if (!_iceComplete) { _iceComplete = true; _iceCompleteTcs?.TrySetResult(); }
                return;
            }
            if (Trickle)
            {
                EmitSignal(new SignalData
                {
                    Type = "candidate",
                    Candidate = new IceCandidateData
                    {
                        Candidate = candidate.Candidate,
                        SdpMLineIndex = candidate.SdpMLineIndex,
                        SdpMid = candidate.SdpMid
                    }
                });
            }
        };

        if (Initiator)
        {
            // Initiator creates the data channel (W3C: pc.createDataChannel)
            _dc = _pc.CreateDataChannel(ChannelName, new RTCDataChannelOptions { Ordered = true });
            SetupDataChannel(_dc);
            await CreateOffer();
        }
        else
        {
            // Responder waits for data channel from remote (W3C: ondatachannel)
            _pc.OnDataChannel += (RTCDataChannelEvent ev) =>
            {
                _dc = ev.Channel;
                ChannelName = _dc?.Label ?? "";
                if (_dc != null) SetupDataChannel(_dc);
            };
        }
    }

    public override async Task Signal(SignalData data)
    {
        if (Destroyed || _pc == null) return;

        if (data.Candidate != null && !string.IsNullOrEmpty(data.Candidate.Candidate))
        {
            if (_pc.RemoteDescription != null)
            {
                using var candidate = new RTCIceCandidate(new RTCIceCandidateInfo
                {
                    Candidate = data.Candidate.Candidate,
                    SdpMLineIndex = data.Candidate.SdpMLineIndex,
                    SdpMid = data.Candidate.SdpMid
                });
                await _pc.AddIceCandidate(candidate);
            }
        }

        if (!string.IsNullOrEmpty(data.Sdp))
        {
            var desc = new RTCSessionDescription { Type = data.Type, Sdp = data.Sdp };
            await _pc.SetRemoteDescription(desc);

            if (data.Type == "offer")
                await CreateAnswer();
        }
    }

    private async Task CreateOffer()
    {
        var offer = await _pc!.CreateOffer();
        await _pc.SetLocalDescription(offer);

        if (!Trickle) await WaitForIceComplete();

        var localDesc = _pc.LocalDescription;
        var sdp = FilterTrickle(localDesc?.Sdp ?? offer.Sdp ?? "");
        EmitSignal(new SignalData { Type = "offer", Sdp = sdp });
    }

    private async Task CreateAnswer()
    {
        var answer = await _pc!.CreateAnswer();
        await _pc.SetLocalDescription(answer);

        if (!Trickle) await WaitForIceComplete();

        var localDesc = _pc.LocalDescription;
        var sdp = FilterTrickle(localDesc?.Sdp ?? answer.Sdp ?? "");
        EmitSignal(new SignalData { Type = "answer", Sdp = sdp });
    }

    private async Task WaitForIceComplete()
    {
        if (_iceComplete) return;
        _iceCompleteTcs = new TaskCompletionSource();
        var timeout = Task.Delay(IceCompleteTimeout);
        await Task.WhenAny(_iceCompleteTcs.Task, timeout);
        _iceComplete = true;
    }

    private void SetupDataChannel(RTCDataChannel dc)
    {
        // W3C: binaryType = "arraybuffer"
        dc.BinaryType = "arraybuffer";

        // W3C: onopen
        dc.OnOpen += (RTCDataChannelEvent e) =>
        {
            _ = ExtractRemoteAddressAsync();
            EmitConnect();
            _openTcs.TrySetResult();
        };

        // Already open? (can happen with SipSorcery pattern)
        if (dc.ReadyState == "open")
        {
            _ = ExtractRemoteAddressAsync();
            EmitConnect();
            _openTcs.TrySetResult();
        }

        // W3C: onclose
        dc.OnClose += (Event e) =>
        {
            EmitDisconnect();
            EmitClose();
        };

        // W3C: onmessage — exact pattern from working WebRtcTransport.OnDataChannelMessage
        dc.OnMessage += OnDataChannelMessage;

        // Chrome "closing" state workaround (matches simple-peer)
        bool isClosing = false;
        _closingTimer = new Timer(_ =>
        {
            if (_dc?.ReadyState == "closing")
            {
                if (isClosing) EmitClose();
                isClosing = true;
            }
            else isClosing = false;
        }, null, ChannelClosingTimeout, ChannelClosingTimeout);
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
            bytes = System.Text.Encoding.UTF8.GetBytes(text ?? "");
        }
        if (bytes != null && bytes.Length > 0)
            EmitData(bytes);
    }

    /// <summary>Extract remote peer IP from WebRTC stats (W3C getStats API).</summary>
    private async Task ExtractRemoteAddressAsync()
    {
        try
        {
            if (_pc == null) return;
            using var stats = await _pc.GetStats();
            var entries = stats.Values();

            // Find the nominated succeeded candidate pair
            string? remoteCandidateId = null;
            foreach (var entry in entries)
            {
                using var typed = entry.Typed();
                if (typed is RTCIceCandidatePairStats pair && pair.Nominated == true && pair.State == "succeeded")
                {
                    remoteCandidateId = pair.RemoteCandidateId;
                    break;
                }
            }

            if (remoteCandidateId == null) return;

            // Look up the remote candidate to get its address
            foreach (var entry in entries)
            {
                if (entry.Id == remoteCandidateId)
                {
                    using var typed = entry.Typed();
                    if (typed is RTCIceCandidateStats candidate)
                    {
                        RemoteAddress = candidate.Address;
                        break;
                    }
                }
            }
        }
        catch { }
    }

    public override async Task Send(byte[] data)
    {
        if (Destroyed || _dc == null || _dc.ReadyState != "open")
            throw new InvalidOperationException("Cannot send: not connected");

        // Backpressure: wait if buffered amount is high (matches simple-peer MAX_BUFFERED_AMOUNT)
        while (_dc.BufferedAmount > MaxBufferedAmount)
        {
            await Task.Delay(10);
            if (Destroyed) return;
        }

        _dc.Send(data);
    }

    public override async Task WaitForOpenAsync(CancellationToken ct = default)
    {
        using var reg = ct.Register(() => _openTcs.TrySetCanceled());
        await _openTcs.Task;
    }

    public override async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;
        Connected = false;

        _closingTimer?.Dispose();
        _openTcs.TrySetCanceled();

        try { _dc?.Close(); } catch { }
        try { _pc?.Close(); } catch { }

        _dc?.Dispose();
        _pc?.Dispose();
        _dc = null;
        _pc = null;

        EmitClose();
    }
}
