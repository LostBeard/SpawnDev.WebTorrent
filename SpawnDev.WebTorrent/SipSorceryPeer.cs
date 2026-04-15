using SIPSorcery.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Desktop WebRTC peer connection via SIPSorcery.
/// Platform-specific implementation of SimplePeer for .NET desktop.
/// Patterns taken directly from the working SipSorceryWebRtcTransport.cs.
/// </summary>
public class SipSorceryPeer : SimplePeer
{
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dc;
    private bool _iceComplete;
    private TaskCompletionSource? _iceCompleteTcs;
    private readonly TaskCompletionSource _openTcs = new();
    private readonly string[] _iceServers;

    public SipSorceryPeer(bool initiator, string[]? iceServers = null, string? channelName = null, bool trickle = false)
        : base(initiator, channelName, trickle)
    {
        _iceServers = iceServers ?? DefaultIceServers;
    }

    public override async Task InitAsync()
    {
        var config = new RTCConfiguration();
        config.iceServers = new List<RTCIceServer>();
        foreach (var url in _iceServers)
            config.iceServers.Add(new RTCIceServer { urls = url });

        _pc = new RTCPeerConnection(config);

        _pc.onconnectionstatechange += (state) =>
        {
            if (state == RTCPeerConnectionState.disconnected ||
                state == RTCPeerConnectionState.failed ||
                state == RTCPeerConnectionState.closed)
                EmitDisconnect();
        };

        _pc.onicecandidate += (candidate) =>
        {
            // SipSorcery fires this for each candidate
            // We don't trickle — we wait for gathering complete
        };

        _pc.onicegatheringstatechange += (state) =>
        {
            if (state == RTCIceGatheringState.complete)
            {
                _iceComplete = true;
                _iceCompleteTcs?.TrySetResult();
            }
        };

        if (Initiator)
        {
            // Create data channel (initiator)
            var dc = await _pc.createDataChannel(ChannelName, new RTCDataChannelInit
            {
                ordered = true,
            });
            SetupDataChannel(dc);
            await CreateOffer();
        }
        else
        {
            // Wait for data channel from remote (responder)
            _pc.ondatachannel += (dc) =>
            {
                ChannelName = dc.label ?? "";
                SetupDataChannel(dc);
            };
        }
    }

    public override async Task Signal(SignalData data)
    {
        if (Destroyed || _pc == null) return;

        if (!string.IsNullOrEmpty(data.Sdp))
        {
            var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = data.Type == "offer" ? RTCSdpType.offer : RTCSdpType.answer,
                sdp = data.Sdp,
            });

            if (result != SetDescriptionResultEnum.OK)
            {
                EmitError(new Exception($"setRemoteDescription failed: {result}"));
                return;
            }

            if (data.Type == "offer")
                await CreateAnswer();
        }
    }

    private async Task CreateOffer()
    {
        // Pattern matching BrowserPeer + RTLink: create, set local, wait for ICE, read localDescription
        var offer = _pc!.createOffer();
        await _pc.setLocalDescription(offer);

        if (!Trickle) await WaitForIceGatheringAsync();

        // Read from localDescription which now contains gathered ICE candidates
        var sdp = FilterTrickle(_pc.localDescription?.sdp?.ToString() ?? offer.sdp ?? "");
        EmitSignal(new SignalData { Type = "offer", Sdp = sdp });
    }

    private async Task CreateAnswer()
    {
        // Pattern matching BrowserPeer + RTLink: create ONCE, set local, wait for ICE, read localDescription
        var answer = _pc!.createAnswer();
        if (answer == null)
        {
            EmitError(new Exception("createAnswer returned null"));
            return;
        }
        await _pc.setLocalDescription(answer);

        if (!Trickle) await WaitForIceGatheringAsync();

        // Read from localDescription which now contains gathered ICE candidates
        var sdp = FilterTrickle(_pc.localDescription?.sdp?.ToString() ?? answer.sdp ?? "");
        EmitSignal(new SignalData { Type = "answer", Sdp = sdp });
    }

    /// <summary>
    /// Wait for ICE gathering to complete.
    /// Uses onicegatheringstatechange + candidate silence fallback (2s).
    /// Pattern from proven SipSorceryWebRtcTransport.WaitForIceGatheringAsync.
    /// </summary>
    private async Task WaitForIceGatheringAsync(int timeoutMs = 15_000)
    {
        if (_pc == null) return;
        if (_pc.iceGatheringState == RTCIceGatheringState.complete) return;
        if (_iceComplete) return;

        _iceCompleteTcs = new TaskCompletionSource();

        // Fallback: 2s of candidate silence = assume done
        System.Timers.Timer? candidateTimer = null;
        _pc.onicecandidate += (_) =>
        {
            candidateTimer?.Stop();
            candidateTimer = new System.Timers.Timer(2000) { AutoReset = false };
            candidateTimer.Elapsed += (_, _) =>
            {
                _iceComplete = true;
                _iceCompleteTcs?.TrySetResult();
            };
            candidateTimer.Start();
        };

        // Check again in case it completed between our check and subscribing
        if (_pc.iceGatheringState == RTCIceGatheringState.complete)
        {
            _iceComplete = true;
            _iceCompleteTcs.TrySetResult();
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() =>
        {
            _iceComplete = true;
            _iceCompleteTcs?.TrySetResult();
        });

        await _iceCompleteTcs.Task;
        candidateTimer?.Stop();
        candidateTimer?.Dispose();
    }

    private void SetupDataChannel(RTCDataChannel dc)
    {
        _dc = dc;

        dc.onopen += () =>
        {
            ExtractRemoteAddress();
            EmitConnect();
            _openTcs.TrySetResult();
        };

        // SipSorcery can fire ondatachannel with state already open
        if (dc.readyState == RTCDataChannelState.open)
        {
            ExtractRemoteAddress();
            EmitConnect();
            _openTcs.TrySetResult();
        }

        dc.onclose += () =>
        {
            EmitDisconnect();
            EmitClose();
        };

        dc.onmessage += (RTCDataChannel channel, DataChannelPayloadProtocols protocol, byte[] data) =>
        {
            if (data.Length > 0)
                EmitData(data);
        };
    }

    private void ExtractRemoteAddress()
    {
        try
        {
            var iceChannel = _pc?.GetRtpChannel() as SIPSorcery.Net.RtpIceChannel;
            var remoteEP = iceChannel?.NominatedEntry?.RemoteCandidate?.DestinationEndPoint;
            if (remoteEP != null)
            {
                RemoteAddress = remoteEP.Address.ToString();
                RemotePort = remoteEP.Port;
            }
        }
        catch { }
    }

    public override async Task Send(byte[] data)
    {
        if (Destroyed || _dc == null || _dc.readyState != RTCDataChannelState.open)
            throw new InvalidOperationException("Cannot send: not connected");

        _dc.send(data);
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

        _openTcs.TrySetCanceled();

        try { _dc?.close(); } catch { }
        try { _pc?.close(); } catch { }
        _pc?.Dispose();
        _dc = null;
        _pc = null;

        EmitClose();
    }
}
