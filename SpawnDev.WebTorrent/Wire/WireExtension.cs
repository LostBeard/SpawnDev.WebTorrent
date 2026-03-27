namespace SpawnDev.WebTorrent.Wire;

/// <summary>
/// Base class for BitTorrent wire protocol extensions (BEP 10).
/// Extensions register with a name and get assigned an ID during the extension handshake.
///
/// Built-in extensions:
///   - ut_metadata (BEP 9): Lazy metadata download from peers
///   - ut_pex (BEP 11): Peer exchange — share peer lists
///   - lt_donthave: Inform peer a piece is no longer available
///
/// Future extensions:
///   - sd_ai: SpawnDev AI agent communication (group work, model coordination)
///   - sd_discovery: Enhanced peer finding beyond standard DHT/PEX
/// </summary>
public abstract class WireExtension
{
    /// <summary>Extension name as registered in the BEP 10 handshake (e.g., "ut_metadata").</summary>
    public abstract string Name { get; }

    /// <summary>Local extension ID (assigned during handshake negotiation).</summary>
    public int LocalId { get; set; }

    /// <summary>Remote peer's extension ID for this extension (0 = not supported).</summary>
    public int RemoteId { get; set; }

    /// <summary>Whether the remote peer supports this extension.</summary>
    public bool IsSupported => RemoteId != 0;

    /// <summary>Handle an incoming extension message from the peer.</summary>
    public abstract Task HandleMessageAsync(byte[] payload);

    /// <summary>Build extension handshake data (included in BEP 10 handshake).</summary>
    public virtual Dictionary<string, object>? GetHandshakeData() => null;

    /// <summary>Process the peer's extension handshake data.</summary>
    public virtual void ProcessHandshakeData(Dictionary<string, object> data) { }
}

/// <summary>
/// BEP 10 Extension Protocol manager.
/// Handles extension handshake negotiation and message routing.
/// </summary>
public class ExtensionManager
{
    private readonly List<WireExtension> _extensions = new();
    private readonly Dictionary<int, WireExtension> _localIdMap = new();
    private readonly Dictionary<string, WireExtension> _nameMap = new();
    private int _nextLocalId = 1;

    /// <summary>Register an extension. Call before handshake.</summary>
    public void Register(WireExtension ext)
    {
        ext.LocalId = _nextLocalId++;
        _extensions.Add(ext);
        _localIdMap[ext.LocalId] = ext;
        _nameMap[ext.Name] = ext;
    }

    /// <summary>Build the local extension handshake dictionary (m = {name: id, ...}).</summary>
    public Dictionary<string, object> BuildHandshake()
    {
        var m = new Dictionary<string, object>();
        foreach (var ext in _extensions)
            m[ext.Name] = ext.LocalId;

        var handshake = new Dictionary<string, object> { ["m"] = m };

        // Include per-extension data
        foreach (var ext in _extensions)
        {
            var data = ext.GetHandshakeData();
            if (data != null)
            {
                foreach (var (key, value) in data)
                    handshake[key] = value;
            }
        }

        return handshake;
    }

    /// <summary>Process the remote peer's extension handshake.</summary>
    public void ProcessHandshake(Dictionary<string, object> handshake)
    {
        if (handshake.TryGetValue("m", out var mObj) && mObj is Dictionary<string, object> m)
        {
            foreach (var (name, idObj) in m)
            {
                if (_nameMap.TryGetValue(name, out var ext) && idObj is long id)
                    ext.RemoteId = (int)id;
            }
        }

        // Forward per-extension data
        foreach (var ext in _extensions)
            ext.ProcessHandshakeData(handshake);
    }

    /// <summary>Route an incoming extension message to the correct handler.</summary>
    public async Task HandleMessageAsync(int extensionId, byte[] payload)
    {
        // Extension ID 0 = handshake message
        if (extensionId == 0)
        {
            // Parse bencode handshake and process
            // TODO: decode bencoded handshake payload
            return;
        }

        // Find extension by remote's ID assignment for us
        foreach (var ext in _extensions)
        {
            if (ext.LocalId == extensionId)
            {
                await ext.HandleMessageAsync(payload);
                return;
            }
        }
    }

    /// <summary>Get an extension by name.</summary>
    public T? Get<T>() where T : WireExtension
        => _extensions.OfType<T>().FirstOrDefault();

    /// <summary>Get an extension by name string.</summary>
    public WireExtension? Get(string name)
        => _nameMap.TryGetValue(name, out var ext) ? ext : null;
}

/// <summary>
/// ut_metadata (BEP 9): Download torrent metadata from peers.
/// Used when joining via magnet link — metadata isn't available yet.
/// Peers that have the metadata share it in 16KB pieces.
/// </summary>
public class UtMetadataExtension : WireExtension
{
    public override string Name => "ut_metadata";

    /// <summary>Total metadata size in bytes (from handshake).</summary>
    public int MetadataSize { get; private set; }

    /// <summary>Received metadata pieces.</summary>
    private readonly Dictionary<int, byte[]> _pieces = new();

    /// <summary>Fired when complete metadata is assembled.</summary>
    public event Action<byte[]>? OnMetadataComplete;

    public override Dictionary<string, object>? GetHandshakeData()
    {
        // If we have metadata, advertise its size
        if (MetadataSize > 0)
            return new Dictionary<string, object> { ["metadata_size"] = (long)MetadataSize };
        return null;
    }

    public override void ProcessHandshakeData(Dictionary<string, object> data)
    {
        if (data.TryGetValue("metadata_size", out var sizeObj) && sizeObj is long size)
            MetadataSize = (int)size;
    }

    public override Task HandleMessageAsync(byte[] payload)
    {
        // TODO: Parse ut_metadata message (bencode dict + optional data)
        // msg_type: 0=request, 1=data, 2=reject
        // piece: piece index (16KB chunks of metadata)
        return Task.CompletedTask;
    }

    /// <summary>Request a metadata piece from the peer.</summary>
    public byte[] CreateRequest(int pieceIndex)
    {
        // Bencode: d8:msg_typei0e5:piecei{pieceIndex}ee
        return System.Text.Encoding.ASCII.GetBytes($"d8:msg_typei0e5:piecei{pieceIndex}ee");
    }
}

/// <summary>
/// ut_pex (BEP 11): Peer Exchange.
/// Connected peers share their peer lists, reducing tracker dependency.
/// </summary>
public class UtPexExtension : WireExtension
{
    public override string Name => "ut_pex";

    /// <summary>Fired when new peers are received via PEX.</summary>
    public event Action<List<string>>? OnPeersReceived;

    public override Task HandleMessageAsync(byte[] payload)
    {
        // TODO: Parse PEX message (bencode dict with added/dropped peers)
        // "added": compact peer list (6 bytes per peer: 4 IP + 2 port)
        // "dropped": peers that disconnected
        return Task.CompletedTask;
    }
}
