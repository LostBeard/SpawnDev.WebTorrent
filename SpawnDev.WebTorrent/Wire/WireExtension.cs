using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent.Wire;

/// <summary>
/// Base class for BitTorrent wire protocol extensions (BEP 10).
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

    /// <summary>Extension manager this extension is registered with.</summary>
    internal ExtensionManager? Manager { get; set; }

    /// <summary>Send a message to the remote peer via this extension.</summary>
    public async Task SendAsync(byte[] payload)
    {
        if (!IsSupported || Manager?.Wire == null) return;
        await Manager.Wire.SendExtensionMessageAsync(RemoteId, payload);
    }

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
    /// <summary>Number of registered extensions.</summary>
    public int Count => _extensions.Count;
    private readonly List<WireExtension> _extensions = new();
    private readonly Dictionary<int, WireExtension> _localIdMap = new();
    private readonly Dictionary<string, WireExtension> _nameMap = new();
    private int _nextLocalId = 1;

    /// <summary>The WireProtocol this manager belongs to (set by WireProtocol constructor).</summary>
    internal WireProtocol? Wire { get; set; }

    /// <summary>Register an extension. Call before handshake.</summary>
    public void Register(WireExtension ext)
    {
        ext.LocalId = _nextLocalId++;
        ext.Manager = this;
        _extensions.Add(ext);
        _localIdMap[ext.LocalId] = ext;
        _nameMap[ext.Name] = ext;
    }

    /// <summary>Get a registered extension by type.</summary>
    public T? Get<T>() where T : WireExtension
        => _extensions.OfType<T>().FirstOrDefault();

    /// <summary>Get a registered extension by name.</summary>
    public WireExtension? Get(string name)
        => _nameMap.TryGetValue(name, out var ext) ? ext : null;

    /// <summary>Build the local extension handshake dictionary (m = {name: id, ...}).</summary>
    public Dictionary<string, object> BuildHandshake()
    {
        var m = new Dictionary<string, object>();
        foreach (var ext in _extensions)
            m[ext.Name] = ext.LocalId;

        var handshake = new Dictionary<string, object> { ["m"] = m };

        foreach (var ext in _extensions)
        {
            var data = ext.GetHandshakeData();
            if (data != null)
                foreach (var (key, value) in data)
                    handshake[key] = value;
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
                if (_nameMap.TryGetValue(name, out var ext))
                {
                    if (idObj is long id)
                        ext.RemoteId = (int)id;
                    else if (idObj is int intId)
                        ext.RemoteId = intId;
                }
            }
        }

        foreach (var ext in _extensions)
            ext.ProcessHandshakeData(handshake);
    }

    /// <summary>Route an incoming extension message to the correct handler.</summary>
    public async Task HandleMessageAsync(int extensionId, byte[] payload)
    {
        if (extensionId == 0)
        {
            // Extension handshake (bencode dict)
            try
            {
                var (decoded, _) = Bencode.BencodeDecoder.Decode(payload, 0);
                if (decoded is Dictionary<string, object> handshake)
                    ProcessHandshake(handshake);
            }
            catch { }
            return;
        }

        foreach (var ext in _extensions)
        {
            if (ext.LocalId == extensionId)
            {
                await ext.HandleMessageAsync(payload);
                return;
            }
        }
    }

}

/// <summary>
/// ut_metadata (BEP 9): Download torrent metadata from peers.
/// Used when joining via magnet link — metadata isn't available yet.
/// Peers that have the metadata share it in 16KB pieces.
///
/// Protocol:
///   - Extension handshake includes "metadata_size" (total bytes)
///   - Request: {msg_type: 0, piece: N}
///   - Data: {msg_type: 1, piece: N, total_size: S} + raw metadata bytes
///   - Reject: {msg_type: 2, piece: N}
/// </summary>
public class UtMetadataExtension : WireExtension
{
    public override string Name => "ut_metadata";

    private const int PieceSize = 16384; // 16KB per metadata piece
    private const int MsgTypeRequest = 0;
    private const int MsgTypeData = 1;
    private const int MsgTypeReject = 2;

    /// <summary>Total metadata size in bytes (from handshake).</summary>
    public int MetadataSize { get; set; }

    /// <summary>Our local metadata bytes (for serving to peers).</summary>
    public byte[]? LocalMetadata { get; set; }

    /// <summary>Received metadata pieces.</summary>
    private readonly Dictionary<int, byte[]> _receivedPieces = new();

    /// <summary>Fired when complete metadata is assembled and verified.</summary>
    public event Action<byte[]>? OnMetadataComplete;

    /// <summary>Expected info hash for verification.</summary>
    public byte[]? ExpectedInfoHash { get; set; }

    public override Dictionary<string, object>? GetHandshakeData()
    {
        if (LocalMetadata != null)
            return new Dictionary<string, object> { ["metadata_size"] = (long)LocalMetadata.Length };
        return null;
    }

    public override void ProcessHandshakeData(Dictionary<string, object> data)
    {
        if (data.TryGetValue("metadata_size", out var sizeObj))
        {
            if (sizeObj is long size) MetadataSize = (int)size;
            else if (sizeObj is int intSize) MetadataSize = intSize;
        }
    }

    public override Task HandleMessageAsync(byte[] payload)
    {
        // The payload is: bencode dict + optional raw metadata bytes
        // Find the end of the bencode dict to split dict from data
        try
        {
            var (decoded, consumed) = Bencode.BencodeDecoder.Decode(payload, 0);
            if (decoded is not Dictionary<string, object> msg) return Task.CompletedTask;

            int msgType = msg.TryGetValue("msg_type", out var mtObj)
                ? (mtObj is long l ? (int)l : mtObj is int i ? i : -1) : -1;
            int piece = msg.TryGetValue("piece", out var pObj)
                ? (pObj is long pl ? (int)pl : pObj is int pi ? pi : -1) : -1;

            switch (msgType)
            {
                case MsgTypeRequest:
                    HandleRequest(piece);
                    break;
                case MsgTypeData:
                    HandleData(piece, payload, consumed);
                    break;
                case MsgTypeReject:
                    // Peer doesn't have this piece — try another peer
                    break;
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    private void HandleRequest(int pieceIndex)
    {
        if (LocalMetadata == null || !IsSupported) return;

        int offset = pieceIndex * PieceSize;
        if (offset >= LocalMetadata.Length) return;

        int length = Math.Min(PieceSize, LocalMetadata.Length - offset);
        var pieceData = new byte[length];
        Array.Copy(LocalMetadata, offset, pieceData, 0, length);

        // Build response: bencode dict + raw data
        var dictStr = $"d8:msg_typei{MsgTypeData}e5:piecei{pieceIndex}e10:total_sizei{LocalMetadata.Length}ee";
        var dictBytes = Encoding.ASCII.GetBytes(dictStr);
        var response = new byte[dictBytes.Length + pieceData.Length];
        Array.Copy(dictBytes, response, dictBytes.Length);
        Array.Copy(pieceData, 0, response, dictBytes.Length, pieceData.Length);

        _ = SendAsync(response);
    }

    private void HandleData(int pieceIndex, byte[] payload, int dataOffset)
    {
        if (MetadataSize <= 0) return;

        // Extract raw metadata bytes after the bencode dict
        int dataLength = payload.Length - dataOffset;
        if (dataLength <= 0) return;

        var pieceData = new byte[dataLength];
        Array.Copy(payload, dataOffset, pieceData, 0, dataLength);
        _receivedPieces[pieceIndex] = pieceData;

        // Check if we have all pieces
        int totalPieces = (MetadataSize + PieceSize - 1) / PieceSize;
        if (_receivedPieces.Count >= totalPieces)
            TryAssembleMetadata(totalPieces);
    }

    private void TryAssembleMetadata(int totalPieces)
    {
        var assembled = new byte[MetadataSize];
        int pos = 0;
        for (int i = 0; i < totalPieces; i++)
        {
            if (!_receivedPieces.TryGetValue(i, out var piece)) return; // missing piece
            int copyLen = Math.Min(piece.Length, MetadataSize - pos);
            Array.Copy(piece, 0, assembled, pos, copyLen);
            pos += copyLen;
        }

        // Verify info hash
        if (ExpectedInfoHash != null)
        {
            var hash = SHA1.HashData(assembled);
            if (!hash.SequenceEqual(ExpectedInfoHash))
            {
                // Hash mismatch — discard and re-request
                _receivedPieces.Clear();
                return;
            }
        }

        _receivedPieces.Clear();
        OnMetadataComplete?.Invoke(assembled);
    }

    /// <summary>Request all metadata pieces from the peer.</summary>
    public void RequestAllPieces()
    {
        if (MetadataSize <= 0 || !IsSupported) return;

        int totalPieces = (MetadataSize + PieceSize - 1) / PieceSize;
        for (int i = 0; i < totalPieces; i++)
        {
            if (!_receivedPieces.ContainsKey(i))
            {
                var request = CreateRequest(i);
                _ = SendAsync(request);
            }
        }
    }

    /// <summary>Create a metadata request message.</summary>
    public byte[] CreateRequest(int pieceIndex)
    {
        return Encoding.ASCII.GetBytes($"d8:msg_typei{MsgTypeRequest}e5:piecei{pieceIndex}ee");
    }
}

/// <summary>
/// ut_pex (BEP 11): Peer Exchange.
/// Connected peers share their peer lists, reducing tracker dependency.
///
/// Protocol:
///   - "added": compact peer list (6 bytes per IPv4 peer: 4 IP + 2 port)
///   - "added.f": flags for each added peer (1 byte per peer)
///   - "dropped": compact peer list of disconnected peers
/// </summary>
public class UtPexExtension : WireExtension
{
    public override string Name => "ut_pex";

    /// <summary>Fired when new peers are received via PEX.</summary>
    public event Action<List<string>>? OnPeersReceived;

    public override Task HandleMessageAsync(byte[] payload)
    {
        try
        {
            var (decoded, _) = Bencode.BencodeDecoder.Decode(payload, 0);
            if (decoded is not Dictionary<string, object> msg) return Task.CompletedTask;

            var peers = new List<string>();

            // Parse "added" compact peer list (6 bytes per IPv4 peer)
            if (msg.TryGetValue("added", out var addedObj) && addedObj is byte[] added)
            {
                for (int i = 0; i + 6 <= added.Length; i += 6)
                {
                    var ip = $"{added[i]}.{added[i + 1]}.{added[i + 2]}.{added[i + 3]}";
                    var port = (added[i + 4] << 8) | added[i + 5];
                    peers.Add($"{ip}:{port}");
                }
            }
            // Also handle string-encoded added (some implementations)
            else if (msg.TryGetValue("added", out var addedStrObj) && addedStrObj is string addedStr)
            {
                var addedBytes = Encoding.Latin1.GetBytes(addedStr);
                for (int i = 0; i + 6 <= addedBytes.Length; i += 6)
                {
                    var ip = $"{addedBytes[i]}.{addedBytes[i + 1]}.{addedBytes[i + 2]}.{addedBytes[i + 3]}";
                    var port = (addedBytes[i + 4] << 8) | addedBytes[i + 5];
                    peers.Add($"{ip}:{port}");
                }
            }

            if (peers.Count > 0)
                OnPeersReceived?.Invoke(peers);
        }
        catch { }

        return Task.CompletedTask;
    }
}
