namespace SpawnDev.WebTorrent.Transports;

/// <summary>
/// Interface for WebRTC transports (browser and desktop).
/// Extends ITransport with WebRTC signaling methods for offer/answer relay.
/// </summary>
public interface IWebRtcTransport : ITransport
{
    /// <summary>Fired when an outgoing offer needs to be sent via the tracker.</summary>
    event Action<string, object>? OnOfferCreated;

    /// <summary>Handle an incoming WebRTC offer and create an answer.</summary>
    Task<(IConnection connection, object answer)> HandleOfferAsync(
        string fromPeerId, object offer, CancellationToken ct = default);

    /// <summary>Handle an incoming WebRTC answer (for an offer we sent).</summary>
    Task HandleAnswerAsync(string fromPeerId, object answer);
}
