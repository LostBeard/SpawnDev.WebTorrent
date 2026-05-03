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
    private bool _dcEverOpen;

    /// <summary>
    /// Last connection state observed via the OnConnectionStateChange event chain. Captures
    /// synthesised states from <c>SpawnDev.RTC.Browser.BrowserRTCPeerConnection</c>'s
    /// iceDisconnected debounce poller, which calls <c>OnConnectionStateChange("failed")</c>
    /// without the underlying JS native <c>connectionState</c> property updating
    /// (Chromium-under-Playwright bug where the native value stays stuck at <c>"connected"</c>
    /// forever after remote tab close). The native query in <see cref="IsTransportDead"/>
    /// can therefore lie about death; this field is the authoritative effective state
    /// because it observes both real and synthesised transitions.
    /// </summary>
    private string? _lastObservedPcState;

    /// <summary>
    /// Reports the underlying transport as dead when ANY of:
    /// (1) the last-observed PC state (which captures synthesised "failed" from
    /// BrowserRTCPeerConnection's poller as well as real native transitions) is
    /// <c>"failed"</c>/<c>"closed"</c>; (2) the native PC state is terminal (covers
    /// platforms whose state changes never went through our event chain); (3) the data
    /// channel was once <c>"open"</c> and is no longer. State <c>"new"</c>/<c>"connecting"</c>
    /// is NOT reported as dead — those are legitimate handshake transitions. Throws are
    /// caught and treated as dead (a disposed PC is a dead PC).
    /// Used by <c>SpawnDev.ILGPU.P2P.P2PWebRtcBridge</c> to filter phantom-alive wires
    /// whose <see cref="SimplePeer.Destroyed"/> flag has not yet been set because the
    /// close-event chain is still propagating.
    /// </summary>
    public override bool IsTransportDead
    {
        get
        {
            try
            {
                if (_lastObservedPcState == "failed" || _lastObservedPcState == "closed") return true;
                if (_pc == null) return true;
                var pcState = _pc.ConnectionState;
                if (pcState == "failed" || pcState == "closed") return true;
                if (_dcEverOpen)
                {
                    var dcState = _dc?.ReadyState;
                    if (dcState != "open") return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

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
                // the initiator sees.
                ChannelName = channel.Label;
                if (WebTorrentClient.VerboseLogging)
                    Console.WriteLine($"[RtcPeer] Responder ChannelName set to '{ChannelName}' (Label='{channel.Label}')");
                WireDataChannel(_dc);
            };

            _pc.OnIceCandidate += HandleIceCandidate;
        }

        _pc.OnConnectionStateChange += state =>
        {
            // Capture the effective state for IsTransportDead. This event fires for both
            // real native transitions AND synthesised ones (BrowserRTCPeerConnection's
            // 15s iceDisconnected debounce poller invokes us with "failed" when the JS
            // native connectionState gets stuck at "connected" / "disconnected" -
            // Chromium-under-Playwright bug on remote tab close). Reading the native
            // value at IsTransportDead time would miss the synthesised transition.
            _lastObservedPcState = state;

            if (WebTorrentClient.VerboseLogging || (state == "failed" || state == "closed"))
                Console.WriteLine($"[RtcPeer][PC-DIAG] state={state} dcReady={_dc?.ReadyState ?? "null"} channelName={ChannelName}");

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
        // SCTP backpressure: ask the data channel to fire OnBufferedAmountLow once the
        // outbound queue drops below MaxBufferedAmount. Send() awaits that signal when
        // BufferedAmount exceeds the threshold instead of blindly stuffing bytes into
        // an already-saturated SCTP send buffer. Prior to this gate, multi-MB push-back
        // (Mandelbrot strip auto-push, 1MB+ output buffers) would saturate SCTP and
        // surface as User-Initiated Abort (cause 12) on the remote end - the remote
        // browser closes the channel when its receive side detects the runaway buffer.
        channel.BufferedAmountLowThreshold = MaxBufferedAmount;
        channel.OnBufferedAmountLow += () =>
        {
            // ALL concurrent Send() callers reference the SAME _bufferedAmountLowTcs
            // by capture; OnBufferedAmountLow completes the shared TCS to release
            // every awaiter at once, then atomically installs a fresh TCS for the
            // next round of waiters.
            var fresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = System.Threading.Interlocked.Exchange(ref _bufferedAmountLowTcs, fresh);
            completed?.TrySetResult(true);
        };

        channel.OnOpen += () =>
        {
            _dcEverOpen = true;
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
            // Always emit on close - this is a terminal signal worth capturing in
            // production logs to diagnose mid-dispatch peer disconnects (data
            // channel closes are the most common cause).
            Console.WriteLine($"[RtcPeer][CH-CLOSE-DIAG] label={channel.Label} bufferedAmount={channel.BufferedAmount} channelName={ChannelName}");
            EmitDisconnect();
            EmitClose();
        };

        channel.OnError += err =>
        {
            Console.WriteLine($"[RtcPeer][CH-ERROR-DIAG] err={err} channelName={ChannelName}");
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
            _dcEverOpen = true;
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

    public override async Task Send(byte[] data)
    {
        if (_dc == null || _dc.ReadyState != "open")
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[RtcPeer] Send BLOCKED: dc={_dc?.ReadyState ?? "null"} len={data.Length}");
            throw new InvalidOperationException("Data channel is not open.");
        }

        // SCTP backpressure: if the outbound queue is already saturated, wait for it
        // to drain below MaxBufferedAmount before stuffing more bytes in. WebRTC's
        // RTCDataChannel.send() does not throw on a full SCTP buffer - it queues
        // unbounded internally, and once the remote receive side detects the runaway
        // it closes the channel with sctpCauseCode=12 (User-Initiated Abort). Awaiting
        // OnBufferedAmountLow keeps the in-flight payload bounded.
        //
        // The per-await ceiling guards against a stalled wire deadlocking the send
        // path. NOT a wall-clock budget for the whole transfer - each iteration
        // resets, so a steadily-draining wire keeps making progress for as long as
        // the consumer wants. The ceiling needs to be long enough for one chunk's
        // worth of SCTP drain even under congestion: at ~1 MB/s effective desktop
        // SCTP throughput, draining 64 KB takes ~64 ms; the 120-second per-await
        // ceiling absorbs CPU stalls, multi-PC handshake thrashing, and similar
        // transient pressure (the prior 30 s ceiling fired during legitimate slow
        // drain on `LargeBuffer_100MB_DispatchedOverRealWebRtc_BitExact`, which
        // pushed back ~256 KB chunks at ~1 MB/s during heavy duplicate-handshake
        // cleanup; the timeout would trip mid-drain and Destroy the peer with
        // "TimeoutException: The operation has timed out").
        // Polling-based backpressure wait: the OnBufferedAmountLow event is best-
        // effort. On desktop the poller in DesktopRTCDataChannel fires only on a
        // strict above->below transition observed between two ~20ms polls; if
        // BufferedAmount overshoots threshold and drains back BETWEEN poll ticks
        // (rapid SCTP drain), the poller misses the transition entirely and
        // wasAboveThreshold stays false. The event then never fires for the next
        // wait cycle even after the buffer has fully drained. Diagnosed
        // 2026-04-29 against `LargeBuffer_100MB_DispatchedOverRealWebRtc_BitExact`:
        // BufferedAmount=0 at the 120-second timeout, ReadyState=open - buffer
        // drained, no event ever fired.
        //
        // Fix: combine the event with a 50ms polling re-check. The event is still
        // honored (resolves the wait promptly when it fires); the poll guarantees
        // forward progress when the event is missed. Each iteration of the loop
        // body waits at most 50ms, so the worst-case overhead is bounded. The
        // 120s ceiling now covers wall-clock-stalled SCTP rather than relying on
        // an event we can't trust.
        var sendStart = DateTime.UtcNow;
        while (_dc.BufferedAmount > MaxBufferedAmount && !Destroyed && _dc.ReadyState == "open")
        {
            var tcs = EnsureBufferedAmountLowTcs();
            if (_dc.BufferedAmount <= MaxBufferedAmount) break;
            try
            {
                // Race the event against a short polling tick. Whichever wakes
                // first wins; the loop's top-of-iteration BufferedAmount check
                // then either breaks out (drained) or installs a new TCS (still
                // saturated) for the next wait round.
                await Task.WhenAny(tcs.Task, Task.Delay(50)).ConfigureAwait(false);
            }
            catch
            {
                // Defensive: WhenAny shouldn't throw, but if it does, fall through
                // to the loop check rather than killing the peer.
            }
            // Wall-clock guard: 120 seconds with NO observed drain progress is a
            // legitimate stall, not just a missed event. Throw so the caller
            // (Peer.SendRaw) can Destroy the wire and let the dispatcher retry.
            if ((DateTime.UtcNow - sendStart).TotalSeconds >= 120)
            {
                Console.WriteLine($"[RtcPeer][BACKPRESSURE-STALL] BufferedAmount={_dc.BufferedAmount} MaxBuffered={MaxBufferedAmount} elapsed={((DateTime.UtcNow - sendStart).TotalSeconds):F1}s ReadyState={_dc.ReadyState} dataLen={data.Length}");
                throw new TimeoutException(
                    $"SCTP backpressure stall: BufferedAmount={_dc.BufferedAmount} stayed above {MaxBufferedAmount} for 120s.");
            }
        }

        if (Destroyed || _dc.ReadyState != "open")
            throw new InvalidOperationException(
                "Data channel closed during backpressure wait.");

        if (WebTorrentClient.VerboseLogging && !_firstSendLogged)
        {
            _firstSendLogged = true;
            Console.WriteLine($"[RtcPeer] First Send {data.Length} bytes");
        }
        _dc.Send(data);
    }

    private TaskCompletionSource<bool>? _bufferedAmountLowTcs;
    private bool _firstSendLogged;
    private static bool _firstOfferLogged;

    /// <summary>
    /// Lazily install the shared backpressure TCS. Called from <see cref="Send"/>
    /// before each await; ensures the slot is non-null even if no
    /// OnBufferedAmountLow event has fired yet (e.g., the first Send hits the
    /// threshold before any drain has occurred).
    /// </summary>
    private TaskCompletionSource<bool> EnsureBufferedAmountLowTcs()
    {
        var existing = System.Threading.Volatile.Read(ref _bufferedAmountLowTcs);
        if (existing != null && !existing.Task.IsCompleted) return existing;
        var fresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prior = System.Threading.Interlocked.CompareExchange(ref _bufferedAmountLowTcs, fresh, existing);
        // If CompareExchange lost the race, prior holds whatever the winner installed.
        // Either way, the live TCS for any other awaiter is the current slot value.
        return System.Threading.Volatile.Read(ref _bufferedAmountLowTcs)!;
    }

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
