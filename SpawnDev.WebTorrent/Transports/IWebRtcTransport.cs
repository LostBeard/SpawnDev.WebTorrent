using System.Text.Json;

namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// Strongly-typed SDP message for WebRTC signaling.
/// Replaces loose "object" passing — SDP is always type + sdp string.
/// </summary>
public record SdpMessage(string Type, string Sdp)
{
    /// <summary>Parse an SDP message from a JsonElement (tracker input).</summary>
    public static SdpMessage FromJson(JsonElement json)
    {
        var type = json.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var sdp = json.TryGetProperty("sdp", out var s) ? s.GetString() ?? "" : "";
        return new SdpMessage(type, sdp);
    }
}

/// <summary>
/// Interface for WebRTC transports (browser and desktop).
/// Extends ITransport with WebRTC signaling methods for offer/answer relay.
/// </summary>
public interface IWebRtcTransport : ITransport
{
    /// <summary>Fired when an outgoing offer needs to be sent via the tracker.</summary>
    event Action<string, object>? OnOfferCreated;

    /// <summary>
    /// Pre-generate a WebRTC offer for sending with tracker announce.
    /// Returns the offer SDP and stores the pending connection keyed by offerId.
    /// </summary>
    Task<(SdpMessage offer, IConnection connection)> CreateOfferAsync(string offerId, CancellationToken ct = default);

    /// <summary>Handle an incoming WebRTC offer (from tracker as JsonElement) and create an answer.</summary>
    Task<(IConnection connection, SdpMessage answer)> HandleOfferAsync(
        string fromPeerId, JsonElement offer, CancellationToken ct = default);

    /// <summary>Handle an incoming WebRTC answer (from tracker as JsonElement, matched by peer ID).</summary>
    Task HandleAnswerAsync(string fromPeerId, JsonElement answer);

    /// <summary>Handle an incoming WebRTC answer for a pre-generated offer (matched by offerId).</summary>
    Task<IConnection?> HandleAnswerByOfferIdAsync(string offerId, JsonElement answer);

    /// <summary>
    /// Create the platform-appropriate WebRTC transport.
    /// Browser: SpawnDev.BlazorJS RTCPeerConnection.
    /// Desktop: SIPSorcery RTCPeerConnection.
    /// </summary>
    static IWebRtcTransport Create(WebRtcTransportOptions? options = null)
    {
        if (OperatingSystem.IsBrowser())
            return new WebRtcTransport(options);
        return new SipSorceryWebRtcTransport(options);
    }
}
