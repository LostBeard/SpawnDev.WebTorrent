using SpawnDev.RTC;

namespace SpawnDev.WebTorrent;

/// <summary>
/// SimplePeer implementation backed by SpawnDev.RTC.
/// Cross-platform - works on both browser (Blazor WASM) and desktop (.NET).
/// Replaces the separate BrowserPeer and SipSorceryPeer implementations
/// with a single unified transport via SpawnDev.RTC's cross-platform WebRTC.
/// </summary>
public class RtcPeer : SimplePeer
{
    private IRTCPeerConnection? _pc;
    private IRTCDataChannel? _dc;
    private readonly string[] _iceServers;
    private TaskCompletionSource<bool>? _openTcs;

    public RtcPeer(bool initiator, string[]? iceServers = null, bool trickle = false)
        : base(initiator, trickle: trickle)
    {
        _iceServers = iceServers ?? DefaultIceServers;
    }

    public override async Task InitAsync()
    {
        var config = new RTCPeerConnectionConfig
        {
            IceServers = _iceServers.Select(url => new RTCIceServerConfig { Urls = new[] { url } }).ToArray(),
        };

        _pc = RTCPeerConnectionFactory.Create(config);
        _openTcs = new TaskCompletionSource<bool>();

        if (Initiator)
        {
            // Create data channel, then create offer
            _dc = _pc.CreateDataChannel(ChannelName);
            WireDataChannel(_dc);

            _pc.OnIceCandidate += HandleIceCandidate;

            var offer = await _pc.CreateOffer();
            await _pc.SetLocalDescription(offer);

            if (!Trickle)
            {
                // Wait for ICE gathering to complete before emitting the offer
                // The offer SDP already contains candidates after SetLocalDescription
                // on SipSorcery (it gathers synchronously), but browser gathers async.
                // Give a brief delay for gathering, then emit.
                await Task.Delay(100);
            }

            EmitSignal(new SignalData
            {
                Type = "offer",
                Sdp = _pc.LocalDescription?.Sdp,
            });
        }
        else
        {
            // Responder: wait for incoming data channel
            _pc.OnDataChannel += channel =>
            {
                _dc = channel;
                WireDataChannel(_dc);
            };

            _pc.OnIceCandidate += HandleIceCandidate;
        }

        _pc.OnConnectionStateChange += state =>
        {
            if (state == "connected")
            {
                EmitConnect();
                _openTcs?.TrySetResult(true);
            }
            else if (state == "disconnected")
            {
                EmitDisconnect();
            }
            else if (state == "failed" || state == "closed")
            {
                EmitDisconnect();
                EmitClose();
                _openTcs?.TrySetResult(false);
            }
        };
    }

    private void WireDataChannel(IRTCDataChannel channel)
    {
        channel.OnOpen += () =>
        {
            EmitConnect();
            _openTcs?.TrySetResult(true);
        };

        channel.OnBinaryMessage += data => EmitData(data);

        channel.OnStringMessage += msg =>
        {
            // BitTorrent wire protocol is binary, but handle string messages gracefully
            EmitData(System.Text.Encoding.UTF8.GetBytes(msg));
        };

        channel.OnClose += () =>
        {
            EmitDisconnect();
            EmitClose();
        };

        channel.OnError += err => EmitError(new Exception(err));
    }

    private void HandleIceCandidate(RTCIceCandidateInit candidate)
    {
        if (Trickle && !string.IsNullOrEmpty(candidate.Candidate))
        {
            EmitSignal(new SignalData
            {
                Type = "candidate",
                Candidate = new IceCandidateData
                {
                    Candidate = candidate.Candidate,
                    SdpMLineIndex = candidate.SdpMLineIndex,
                    SdpMid = candidate.SdpMid,
                },
            });
        }
    }

    public override async Task Signal(SignalData data)
    {
        if (_pc == null) throw new InvalidOperationException("Peer not initialized. Call InitAsync() first.");

        if (data.Type == "offer")
        {
            await _pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = data.Sdp ?? "" });
            var answer = await _pc.CreateAnswer();
            await _pc.SetLocalDescription(answer);

            if (!Trickle)
            {
                await Task.Delay(100);
            }

            EmitSignal(new SignalData
            {
                Type = "answer",
                Sdp = _pc.LocalDescription?.Sdp,
            });
        }
        else if (data.Type == "answer")
        {
            await _pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "answer", Sdp = data.Sdp ?? "" });
        }
        else if (data.Type == "candidate" && data.Candidate != null)
        {
            await _pc.AddIceCandidate(new RTCIceCandidateInit
            {
                Candidate = data.Candidate.Candidate ?? "",
                SdpMid = data.Candidate.SdpMid,
                SdpMLineIndex = data.Candidate.SdpMLineIndex,
            });
        }
    }

    public override Task Send(byte[] data)
    {
        if (_dc == null || _dc.ReadyState != "open")
            throw new InvalidOperationException("Data channel is not open.");
        _dc.Send(data);
        return Task.CompletedTask;
    }

    public override async Task WaitForOpenAsync(CancellationToken ct = default)
    {
        if (_dc?.ReadyState == "open") return;
        if (_openTcs == null) throw new InvalidOperationException("Peer not initialized.");

        using var reg = ct.Register(() => _openTcs.TrySetCanceled());
        await _openTcs.Task;
    }

    public override ValueTask DisposeAsync()
    {
        if (Destroyed) return ValueTask.CompletedTask;
        Destroyed = true;

        _dc?.Dispose();
        _pc?.Close();
        _pc?.Dispose();
        _dc = null;
        _pc = null;

        EmitClose();
        return ValueTask.CompletedTask;
    }
}
