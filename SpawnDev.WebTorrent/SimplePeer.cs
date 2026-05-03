using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Abstract WebRTC peer connection.
/// Matches simple-peer behavior: create offer/answer, signal, send/receive binary data.
/// Platform-specific implementations inherit from this class.
/// Default: RtcPeer (cross-platform via SpawnDev.RTC).
/// </summary>
public abstract class SimplePeer : IAsyncDisposable
{
    // Constants matching JS simple-peer exactly
    public const int MaxBufferedAmount = 64 * 1024;
    public const int IceCompleteTimeout = 5_000;
    public const int ChannelClosingTimeout = 5_000;

    public static readonly string[] DefaultIceServers = new[]
    {
        "stun:stun.l.google.com:19302",
        "stun:global.stun.twilio.com:3478"
    };

    // ========================
    // STATE
    // ========================

    public bool Initiator { get; }
    public string ChannelName { get; protected set; }
    public bool Connected { get; protected set; }
    public bool Destroyed { get; protected set; }
    public bool Trickle { get; set; }
    public string? RemoteAddress { get; protected set; }
    public int? RemotePort { get; protected set; }

    /// <summary>
    /// True when the underlying transport is no longer reachable, regardless of whether the
    /// `Destroyed` flag has been set. Default <c>false</c>; subclasses override with concrete
    /// liveness signals (e.g. <see cref="RtcPeer"/> reports dead when its
    /// RTCPeerConnection has reached <c>"failed"</c>/<c>"closed"</c> or its data channel
    /// transitioned out of <c>"open"</c> after once being open).
    /// Used by SpawnDev.ILGPU.P2P.P2PWebRtcBridge to filter phantom-alive wires whose
    /// `Destroyed` has not yet been set because the connection-state-change event chain
    /// did not propagate (Chromium-under-Playwright bug where `connectionstatechange`
    /// does not fire on remote tab close, leaving Destroyed=false on dead transports).
    /// </summary>
    public virtual bool IsTransportDead => false;

    // ========================
    // EVENTS
    // ========================

    public event Action<SignalData>? OnSignal;
    public event Action? OnConnect;
    public event Action<byte[]>? OnData;
    public event Action? OnDisconnect;
    public event Action? OnClose;
    public event Action<Exception>? OnError;

    protected void EmitSignal(SignalData data) => OnSignal?.Invoke(data);
    protected void EmitConnect() { Connected = true; OnConnect?.Invoke(); }
    protected void EmitData(byte[] data) => OnData?.Invoke(data);
    protected void EmitDisconnect() { Connected = false; OnDisconnect?.Invoke(); }
    protected void EmitClose() => OnClose?.Invoke();
    protected void EmitError(Exception err) => OnError?.Invoke(err);

    // ========================
    // CONSTRUCTOR
    // ========================

    protected SimplePeer(bool initiator, string? channelName = null, bool trickle = false)
    {
        Initiator = initiator;
        Trickle = trickle;
        ChannelName = channelName ?? (initiator
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant()
            : "");
    }

    // ========================
    // ABSTRACT METHODS — platform-specific
    // ========================

    /// <summary>Initialize the peer connection and begin signaling.</summary>
    public abstract Task InitAsync();

    /// <summary>Process incoming signal data (offer, answer, or ICE candidate).</summary>
    public abstract Task Signal(SignalData data);

    /// <summary>Send binary data to the remote peer.</summary>
    public abstract Task Send(byte[] data);

    /// <summary>Wait for the data channel to open.</summary>
    public abstract Task WaitForOpenAsync(CancellationToken ct = default);

    /// <summary>Destroy the connection.</summary>
    public abstract ValueTask DisposeAsync();

    // ========================
    // HELPERS
    // ========================

    protected static string FilterTrickle(string sdp)
        => Regex.Replace(sdp, @"a=ice-options:trickle\s*\r?\n?", "");
}

// ========================
// SIGNALING DATA TYPES
// ========================

public class SignalData
{
    /// <summary>"offer", "answer", or "candidate"</summary>
    public string Type { get; set; } = "";

    /// <summary>SDP string (for offer/answer).</summary>
    public string? Sdp { get; set; }

    /// <summary>ICE candidate (for candidate type).</summary>
    public IceCandidateData? Candidate { get; set; }
}

public class IceCandidateData
{
    public string? Candidate { get; set; }
    public int? SdpMLineIndex { get; set; }
    public string? SdpMid { get; set; }
}
