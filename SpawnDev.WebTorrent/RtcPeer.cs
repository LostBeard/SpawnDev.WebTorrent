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

    /// <summary>
    /// The underlying SpawnDev.RTC <see cref="IRTCPeerConnection"/> for this peer.
    /// <c>null</c> until <see cref="InitAsync"/> runs. Exposed so consumers (e.g.
    /// SpawnDev.ILGPU.P2P) can reach platform-specific knobs that aren't surfaced on the
    /// generic IRTCPeerConnection interface — for example, on desktop cast to
    /// <c>DesktopRTCPeerConnection</c> to reach the SIPSorcery
    /// <c>NativeConnection.sctp.RTCSctpAssociation.MaxBurst</c> /
    /// <c>BurstPeriodMilliseconds</c> tunables added in SpawnDev.SIPSorcery 10.0.5-rc.2.
    /// Browser-path consumers typically don't need this accessor; browser WebRTC doesn't
    /// expose per-connection SCTP tunables and libwebrtc's defaults are already tuned.
    /// </summary>
    public IRTCPeerConnection? PeerConnection => _pc;

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

        if (WebTorrentClient.VerboseLogging)
            Console.WriteLine($"[RtcPeer] InitAsync initiator={Initiator} channel={ChannelName} trickle={Trickle}");

        if (Initiator)
        {
            // Create data channel, then create offer
            _dc = _pc.CreateDataChannel(ChannelName);
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] Initiator created DataChannel label={ChannelName} readyState={_dc.ReadyState}");
            WireDataChannel(_dc);

            _pc.OnIceCandidate += HandleIceCandidate;

            // Non-trickle peers must ship a complete SDP with all candidates (host + srflx).
            // Desktop (SipSorcery): WaitForIceGatheringToComplete=true makes createOffer
            // block internally until STUN is done, so the returned SDP has all candidates.
            // Browser (native): the flag is ignored; createOffer returns the base SDP and
            // gathering starts on SetLocalDescription. localDescription.sdp is then updated
            // as candidates arrive, so we wait for state=complete before reading it.
            var offer = await _pc.CreateOffer(new RTCOfferOptions
            {
                WaitForIceGatheringToComplete = !Trickle,
            });
            await _pc.SetLocalDescription(offer);
            if (!Trickle) await WaitForIceGatheringCompleteAsync();

            if (WebTorrentClient.VerboseLogging)
            {
                var sdp = _pc.LocalDescription?.Sdp ?? "";
                Console.WriteLine($"[RtcPeer] Initiator EmitSignal(offer) gather={_pc.IceGatheringState} sdpLen={sdp.Length} candTypes={SummarizeSdpCandidateTypes(sdp)}");
                if (!_firstOfferLogged)
                {
                    _firstOfferLogged = true;
                    Console.WriteLine($"[RtcPeer] === FIRST OFFER SDP ===\n{sdp}\n=== END SDP ===");
                }
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
                if (WebTorrentClient.VerboseLogging)
                    Console.WriteLine($"[RtcPeer] Responder OnDataChannel label={channel.Label} readyState={channel.ReadyState}");
                _dc = channel;
                // Propagate the channel's label up to SimplePeer so Torrent.OnHandshake's
                // deterministic-tiebreaker can compare the same cross-side-stable identifier
                // the initiator sees. Initiator created the channel with this label; responder
                // defaults to "" until the channel arrives via OnDataChannel — this line
                // closes the gap so both endpoints agree on ChannelName.
                ChannelName = channel.Label;
                WireDataChannel(_dc);
            };

            _pc.OnIceCandidate += HandleIceCandidate;
        }

        _pc.OnConnectionStateChange += state =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] PC state={state} dcReady={_dc?.ReadyState ?? "null"}");

            if (state == "disconnected")
            {
                EmitDisconnect();
            }
            else if (state == "failed" || state == "closed")
            {
                EmitDisconnect();
                EmitClose();
                _openTcs?.TrySetResult(false);
            }
            // NOTE: "connected" intentionally does NOT emit connect.
            // Peer connection "connected" means ICE+DTLS complete, but the SCTP
            // data channel may still be negotiating. Emitting connect here would
            // let consumers call Send() before the channel is open, which throws
            // and destroys the peer. Connect fires from channel.OnOpen only.
        };

        _pc.OnIceConnectionStateChange += state =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] ICE state={state}");
        };

        _pc.OnSignalingStateChange += state =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] Signaling state={state}");
        };

        _pc.OnIceGatheringStateChange += state =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] ICE gathering state={state}");
        };
    }

    private void WireDataChannel(IRTCDataChannel channel)
    {
        channel.OnOpen += () =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] DataChannel OPEN label={channel.Label} initiator={Initiator}");
            EmitConnect();
            _openTcs?.TrySetResult(true);
        };

        channel.OnBinaryMessage += data =>
        {
            if (WebTorrentClient.VerboseLogging && !_firstRecvLogged)
            {
                _firstRecvLogged = true;
                Console.WriteLine($"[RtcPeer] First binary recv {data.Length} bytes");
            }
            EmitData(data);
        };

        channel.OnStringMessage += msg =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] String recv len={msg.Length}");
            // BitTorrent wire protocol is binary, but handle string messages gracefully
            EmitData(System.Text.Encoding.UTF8.GetBytes(msg));
        };

        channel.OnClose += () =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] DataChannel CLOSE");
            EmitDisconnect();
            EmitClose();
        };

        channel.OnError += err =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] DataChannel ERROR: {err}");
            EmitError(new Exception(err));
        };

        // Defensive: if the channel is already open at subscription time (edge case
        // on responder if the browser RTCDataChannel arrives from OnDataChannel
        // already in readyState="open", or SipSorcery fires onopen synchronously),
        // schedule EmitConnect for the NEXT event-loop tick rather than firing
        // synchronously. Synchronous firing means the event goes out before the
        // Torrent layer has subscribed simplePeer.OnConnect to the new Peer — the
        // event is lost, peer.OnConnected never runs, SendHandshake never fires,
        // and the peerCount stays at 0 forever. Deferring one tick gives the
        // Torrent._onAddPeer → `simplePeer.OnConnect += ...` chain a chance to
        // run first so the event has an actual subscriber when it fires.
        if (channel.ReadyState == "open")
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] DataChannel was already OPEN at subscribe time — deferring EmitConnect");
            _ = Task.Run(() =>
            {
                EmitConnect();
                _openTcs?.TrySetResult(true);
            });
        }
    }

    private bool _firstRecvLogged;

    private void HandleIceCandidate(RTCIceCandidateInit candidate)
    {
        if (WebTorrentClient.VerboseLogging)
        {
            var cand = candidate.Candidate ?? "(null)";
            var type = "";
            var typIdx = cand.IndexOf(" typ ");
            if (typIdx >= 0)
            {
                var rest = cand.Substring(typIdx + 5);
                var sp = rest.IndexOf(' ');
                type = sp > 0 ? rest.Substring(0, sp) : rest;
            }
            Console.WriteLine($"[RtcPeer] ICE candidate initiator={Initiator} type={type} cand={cand}");
        }
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

        if (WebTorrentClient.VerboseLogging)
            Console.WriteLine($"[RtcPeer] Signal recv type={data.Type} sdpLen={data.Sdp?.Length ?? 0}");

        if (data.Type == "offer")
        {
            await _pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = data.Sdp ?? "" });
            var answer = await _pc.CreateAnswer(new RTCAnswerOptions
            {
                WaitForIceGatheringToComplete = !Trickle,
            });
            await _pc.SetLocalDescription(answer);
            if (!Trickle) await WaitForIceGatheringCompleteAsync();

            if (WebTorrentClient.VerboseLogging)
            {
                var sdp = _pc.LocalDescription?.Sdp ?? "";
                Console.WriteLine($"[RtcPeer] Responder EmitSignal(answer) gather={_pc.IceGatheringState} sdpLen={sdp.Length} candTypes={SummarizeSdpCandidateTypes(sdp)}");
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
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] Send BLOCKED: dc={_dc?.ReadyState ?? "null"} len={data.Length}");
            throw new InvalidOperationException("Data channel is not open.");
        }
        if (WebTorrentClient.VerboseLogging && !_firstSendLogged)
        {
            _firstSendLogged = true;
            Console.WriteLine($"[RtcPeer] First Send {data.Length} bytes");
        }
        _dc.Send(data);
        return Task.CompletedTask;
    }

    private bool _firstSendLogged;
    private static bool _firstOfferLogged;

    public override async Task WaitForOpenAsync(CancellationToken ct = default)
    {
        if (_dc?.ReadyState == "open") return;
        if (_openTcs == null) throw new InvalidOperationException("Peer not initialized.");

        using var reg = ct.Register(() => _openTcs.TrySetCanceled());
        await _openTcs.Task;
    }

    // Matches JS simple-peer IceCompleteTimeout (5000ms). On desktop, SipSorcery's
    // createOffer already blocked for gathering so this returns immediately. On
    // browser, we wait here for native WebRTC to finish gathering and update
    // localDescription.sdp with host + srflx candidates.
    private async Task WaitForIceGatheringCompleteAsync(int timeoutMs = 5000)
    {
        if (_pc == null) return;
        if (_pc.IceGatheringState == "complete") return;

        var tcs = new TaskCompletionSource();
        Action<string> handler = state =>
        {
            if (state == "complete") tcs.TrySetResult();
        };
        _pc.OnIceGatheringStateChange += handler;
        try
        {
            if (_pc.IceGatheringState == "complete")
            {
                tcs.TrySetResult();
            }
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task && WebTorrentClient.VerboseLogging)
            {
                Console.WriteLine($"[RtcPeer] ICE gathering timeout after {timeoutMs}ms, state={_pc.IceGatheringState}");
            }
        }
        finally
        {
            _pc.OnIceGatheringStateChange -= handler;
        }
    }

    private static string SummarizeSdpCandidateTypes(string sdp)
    {
        if (string.IsNullOrEmpty(sdp)) return "none";
        int host = 0, srflx = 0, prflx = 0, relay = 0, other = 0;
        foreach (var line in sdp.Split('\n'))
        {
            var l = line.Trim();
            if (!l.StartsWith("a=candidate:")) continue;
            var typIdx = l.IndexOf(" typ ");
            if (typIdx < 0) { other++; continue; }
            var rest = l.Substring(typIdx + 5);
            var sp = rest.IndexOf(' ');
            var t = sp > 0 ? rest.Substring(0, sp) : rest;
            switch (t)
            {
                case "host": host++; break;
                case "srflx": srflx++; break;
                case "prflx": prflx++; break;
                case "relay": relay++; break;
                default: other++; break;
            }
        }
        return $"host={host},srflx={srflx},prflx={prflx},relay={relay},other={other}";
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
