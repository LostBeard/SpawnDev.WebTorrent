namespace SpawnDev.WebTorrent.Discovery;

/// <summary>
/// Peer discovery source abstraction. Implementations:
/// - TrackerDiscovery: HTTP/UDP/WebSocket tracker announces
/// - DhtDiscovery: Distributed Hash Table (Kademlia-based)
/// - PexDiscovery: Peer Exchange (BEP 11, over wire)
/// - LsdDiscovery: Local Service Discovery (LAN, desktop only)
/// </summary>
public interface IDiscovery : IAsyncDisposable
{
    /// <summary>Discovery source type.</summary>
    string Type { get; }

    /// <summary>Start discovering peers for the given info hash.</summary>
    Task StartAsync(byte[] infoHash, int port, CancellationToken ct = default);

    /// <summary>Stop discovery.</summary>
    Task StopAsync();

    /// <summary>Announce that we have the torrent (for seeding).</summary>
    Task AnnounceAsync(byte[] infoHash, int port, long uploaded, long downloaded, long left, CancellationToken ct = default);

    /// <summary>Fired when a new peer is discovered.</summary>
    event Action<PeerInfo> OnPeer;
}

/// <summary>
/// Information about a discovered peer.
/// </summary>
public record PeerInfo
{
    /// <summary>Peer address (IP:port for TCP, signaling data for WebRTC).</summary>
    public required string Address { get; init; }

    /// <summary>How this peer was discovered.</summary>
    public required string Source { get; init; }

    /// <summary>Peer ID if known (from tracker response).</summary>
    public byte[]? PeerId { get; init; }
}
