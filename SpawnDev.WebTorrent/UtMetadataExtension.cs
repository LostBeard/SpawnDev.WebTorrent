using SpawnDev.WebTorrent.Bencode;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent;

/// <summary>
/// ut_metadata wire extension (BEP 9) — exchanges torrent metadata between peers.
/// Direct 1:1 port of ut_metadata/index.js from JS WebTorrent.
/// When a torrent is added via magnet link, this extension fetches the info dict
/// from peers who already have it.
/// </summary>
public class UtMetadataExtension : IWireExtension
{
    // Constants matching JS exactly
    public const int MaxMetadataSize = 10_000_000;  // 10 MB
    public const int PieceLength = 1 << 14;         // 16 KiB

    public string Name => "ut_metadata";

    private Wire? _wire;
    private string? _infoHash;
    private bool _fetching;
    private bool _metadataComplete;
    private int _metadataSize;
    private int _numPieces;
    private int _remainingRejects;
    private byte[]? _metadata;
    private bool[]? _bitfield;

    // Pre-existing metadata (if we already have it — e.g., from .torrent file)
    private byte[]? _existingMetadata;

    /// <summary>
    /// Metadata version being exchanged: 1 = legacy BEP 9 (info dict is v1, verified
    /// against SHA-1 InfoHash); 2 = BEP 52 v2 extension (info dict is v2, verified
    /// against SHA-256 <see cref="V2InfoHashHex"/>). Set by the consumer before
    /// <see cref="SetWire"/> is called. Default is 1 to preserve legacy behavior.
    ///
    /// The community-standard "metadata_version" key in the extended handshake is
    /// bumped to 2 when this is 2, telling peers they should expect the v2 info dict.
    /// If both peers advertise version 2, the exchanged bytes are the v2 info dict;
    /// if either side is still version 1, both fall back to v1.
    /// </summary>
    public int MetadataVersion { get; set; } = 1;

    /// <summary>
    /// Full v2 SHA-256 info hash hex (64 chars) used for SHA-256 verification of
    /// received v2 metadata. Required when <see cref="MetadataVersion"/> is 2; ignored
    /// otherwise. Typically sourced from a parsed v2 magnet (<c>xt=urn:btmh:</c>).
    /// </summary>
    public string? V2InfoHashHex { get; set; }

    /// <summary>Peer's advertised metadata version (from their extended handshake).</summary>
    public int PeerMetadataVersion { get; private set; } = 1;

    /// <summary>
    /// When <c>true</c> (default, matches historical behavior), receiving the peer's
    /// extended handshake with a valid <c>metadata_size</c> automatically starts
    /// requesting metadata pieces. Tests with synchronous loopback transport may want
    /// to set this to <c>false</c> and call <see cref="Fetch"/> explicitly after both
    /// sides' extended handshakes have completed, to avoid a timing race where the
    /// responder hasn't yet sent its own extended handshake.
    /// </summary>
    public bool AutoFetchOnHandshake { get; set; } = true;

    /// <summary>Fired when complete metadata is received and verified.</summary>
    public event Action<byte[]>? OnMetadata;

    /// <summary>Fired on warnings (peer doesn't support, invalid data, etc.).</summary>
    public event Action<string>? OnWarning;

    /// <summary>Create extension, optionally with existing metadata to serve to peers.</summary>
    public UtMetadataExtension(byte[]? existingMetadata = null)
    {
        _existingMetadata = existingMetadata;
        if (existingMetadata != null)
        {
            _metadataComplete = true;
            _metadata = existingMetadata;
            _metadataSize = existingMetadata.Length;
        }
    }

    public void OnHandshake(string infoHash, string peerId, WireExtensions extensions)
    {
        _infoHash = infoHash;
    }

    public void OnExtendedHandshake(Dictionary<string, object> handshake)
    {
        // Check peer supports ut_metadata
        if (!handshake.TryGetValue("m", out var mObj) || mObj is not Dictionary<string, object> m)
        {
            OnWarning?.Invoke("Peer does not support ut_metadata");
            return;
        }
        if (!m.ContainsKey("ut_metadata"))
        {
            OnWarning?.Invoke("Peer does not support ut_metadata");
            return;
        }

        // BEP 52-extended: peer may advertise "metadata_version": 2 to indicate it will
        // serve the v2 info dict (SHA-256-verified). Absent → peer is legacy v1.
        if (handshake.TryGetValue("metadata_version", out var mvObj))
        {
            PeerMetadataVersion = mvObj switch
            {
                long l => (int)l,
                int i => i,
                _ => 1
            };
        }

        // If our side is v2 but peer is only v1, we can't do v2 metadata exchange with
        // this peer. Don't request; let the consumer pick a different peer.
        if (MetadataVersion == 2 && PeerMetadataVersion != 2)
        {
            OnWarning?.Invoke("Peer is v1-only ut_metadata; we are v2 — skipping");
            return;
        }

        // Get metadata_size from peer
        if (!handshake.TryGetValue("metadata_size", out var sizeObj))
        {
            OnWarning?.Invoke("Peer does not have metadata");
            return;
        }

        long metadataSize = sizeObj switch
        {
            long l => l,
            int i => i,
            _ => 0
        };

        if (metadataSize <= 0 || metadataSize > MaxMetadataSize)
        {
            OnWarning?.Invoke($"Peer gave invalid metadata size: {metadataSize}");
            return;
        }

        _metadataSize = (int)metadataSize;
        _numPieces = (int)Math.Ceiling((double)_metadataSize / PieceLength);
        _remainingRejects = _numPieces * 2;

        // Start fetching if we don't have metadata yet. Opt-outable via
        // AutoFetchOnHandshake so sync-loopback tests can defer requesting until both
        // sides' extended handshakes have completed.
        if (!_metadataComplete && AutoFetchOnHandshake)
        {
            _fetching = true;
            RequestPieces();
        }
    }

    public void OnMessage(byte[] buf)
    {
        // BEP 9 messages: bencode dict + optional trailer (piece data)
        // Format: d8:msg_typei1e5:piecei0e10:total_sizei31235ee<raw piece data>
        // The dict ends at "ee" — everything after is raw metadata bytes

        Dictionary<string, object>? dict;
        byte[] trailer;

        try
        {
            // Find the end of the bencode dict
            var text = Encoding.ASCII.GetString(buf);
            int trailerIndex = FindBencodeDictEnd(buf);
            if (trailerIndex < 0) return;

            var (parsed, _) = BencodeDecoder.DecodeDictionary(buf, 0);
            dict = parsed;
            trailer = buf[trailerIndex..];
        }
        catch
        {
            return; // drop invalid messages
        }

        if (dict == null) return;

        int msgType = dict.TryGetValue("msg_type", out var mt) ? Convert.ToInt32(mt) : -1;
        int piece = dict.TryGetValue("piece", out var p) ? Convert.ToInt32(p) : 0;

        switch (msgType)
        {
            case 0: // request
                HandleRequest(piece);
                break;
            case 1: // data
                int totalSize = dict.TryGetValue("total_size", out var ts) ? Convert.ToInt32(ts) : 0;
                HandleData(piece, trailer, totalSize);
                break;
            case 2: // reject
                HandleReject(piece);
                break;
        }
    }

    // ========================
    // PUBLIC API
    // ========================

    /// <summary>Start fetching metadata from this peer.</summary>
    public void Fetch()
    {
        if (_metadataComplete) return;
        _fetching = true;
        if (_metadataSize > 0)
            RequestPieces();
    }

    /// <summary>Stop fetching metadata.</summary>
    public void Cancel()
    {
        _fetching = false;
    }

    /// <summary>Set metadata we already have (from .torrent or another peer).</summary>
    public async Task<bool> SetMetadata(byte[] metadata)
    {
        if (_metadataComplete) return true;

        // Verify hash — branch on MetadataVersion. v1: SHA-1 against _infoHash (wire
        // info hash hex, 40 chars). v2: SHA-256 against V2InfoHashHex (full 64-char
        // v2 hash).
        if (MetadataVersion == 2)
        {
            if (string.IsNullOrEmpty(V2InfoHashHex))
                return false; // v2 mode requires a v2 hash target
            var v2Hash = Convert.ToHexString(SHA256.HashData(metadata)).ToLowerInvariant();
            if (v2Hash != V2InfoHashHex.ToLowerInvariant())
                return false;
        }
        else if (_infoHash != null)
        {
            var hash = Convert.ToHexString(SHA1.HashData(metadata)).ToLowerInvariant();
            if (hash != _infoHash)
                return false;
        }

        Cancel();
        _metadata = metadata;
        _metadataComplete = true;
        _metadataSize = metadata.Length;

        OnMetadata?.Invoke(metadata);
        return true;
    }

    /// <summary>Set the wire this extension operates on (called after construction).</summary>
    public void SetWire(Wire wire)
    {
        _wire = wire;

        // Advertise metadata_size in our extended handshake if we have metadata
        if (_metadataComplete && _metadataSize > 0)
            wire.ExtendedHandshake["metadata_size"] = _metadataSize;

        // BEP 52-extended: advertise "metadata_version": 2 when serving the v2 info
        // dict. Peers compare and decide whether to request from us.
        if (MetadataVersion == 2)
            wire.ExtendedHandshake["metadata_version"] = 2L;
    }

    // ========================
    // SEND HELPERS
    // ========================

    private void Send(Dictionary<string, object> dict, byte[]? trailer = null)
    {
        if (_wire == null) return;
        var encoded = BencodeEncoder.Encode(dict);
        byte[] buf;
        if (trailer != null && trailer.Length > 0)
        {
            buf = new byte[encoded.Length + trailer.Length];
            encoded.CopyTo(buf, 0);
            trailer.CopyTo(buf, encoded.Length);
        }
        else
        {
            buf = encoded;
        }
        _ = _wire.Extended("ut_metadata", buf);
    }

    private void SendRequest(int piece)
    {
        Send(new Dictionary<string, object> { ["msg_type"] = 0, ["piece"] = piece });
    }

    private void SendData(int piece, byte[] data, int totalSize)
    {
        var dict = new Dictionary<string, object> { ["msg_type"] = 1, ["piece"] = piece, ["total_size"] = totalSize };
        Send(dict, data);
    }

    private void SendReject(int piece)
    {
        Send(new Dictionary<string, object> { ["msg_type"] = 2, ["piece"] = piece });
    }

    // ========================
    // INCOMING MESSAGE HANDLERS
    // ========================

    private void HandleRequest(int piece)
    {
        if (!_metadataComplete || _metadata == null)
        {
            SendReject(piece);
            return;
        }

        int start = piece * PieceLength;
        int end = Math.Min(start + PieceLength, _metadataSize);
        var buf = _metadata[start..end];
        SendData(piece, buf, _metadataSize);
    }

    private void HandleData(int piece, byte[] buf, int totalSize)
    {
        if (buf.Length > PieceLength || !_fetching || _metadata == null) return;

        int offset = piece * PieceLength;
        if (offset + buf.Length > _metadata.Length) return;

        Array.Copy(buf, 0, _metadata, offset, buf.Length);
        if (_bitfield != null && piece < _bitfield.Length)
            _bitfield[piece] = true;

        CheckDone();
    }

    private void HandleReject(int piece)
    {
        if (_remainingRejects > 0 && _fetching)
        {
            SendRequest(piece);
            _remainingRejects--;
        }
        else
        {
            OnWarning?.Invoke("Peer sent 'reject' too many times");
        }
    }

    // ========================
    // REQUEST / CHECK
    // ========================

    private void RequestPieces()
    {
        if (!_fetching) return;
        _metadata = new byte[_metadataSize];
        _bitfield = new bool[_numPieces];
        for (int piece = 0; piece < _numPieces; piece++)
            SendRequest(piece);
    }

    private async void CheckDone()
    {
        if (_bitfield == null) return;
        for (int i = 0; i < _numPieces; i++)
            if (!_bitfield[i]) return;

        // All pieces received — verify
        var success = await SetMetadata(_metadata!);
        if (!success)
        {
            // Reset and retry
            _bitfield = new bool[_numPieces];
            _remainingRejects -= _numPieces;
            if (_remainingRejects > 0)
                RequestPieces();
            else
                OnWarning?.Invoke("Peer sent invalid metadata");
        }
    }

    // ========================
    // HELPERS
    // ========================

    /// <summary>
    /// Find the end of a bencode dictionary in a byte buffer.
    /// BEP 9 messages are: bencode_dict + raw_trailer_bytes
    /// We need to find where the dict ends to split them.
    /// </summary>
    private static int FindBencodeDictEnd(byte[] buf)
    {
        if (buf.Length == 0 || buf[0] != (byte)'d') return -1;

        int depth = 0;
        int i = 0;
        while (i < buf.Length)
        {
            byte b = buf[i];
            if (b == (byte)'d' || b == (byte)'l')
            {
                depth++;
                i++;
            }
            else if (b == (byte)'e')
            {
                depth--;
                i++;
                if (depth == 0) return i;
            }
            else if (b == (byte)'i')
            {
                // Integer: i<digits>e
                i++;
                while (i < buf.Length && buf[i] != (byte)'e') i++;
                i++; // skip 'e'
            }
            else if (b >= (byte)'0' && b <= (byte)'9')
            {
                // String: <length>:<data>
                int lenStart = i;
                while (i < buf.Length && buf[i] != (byte)':') i++;
                int strLen = int.Parse(Encoding.ASCII.GetString(buf, lenStart, i - lenStart));
                i++; // skip ':'
                i += strLen; // skip string data
            }
            else
            {
                return -1; // invalid
            }
        }
        return -1;
    }
}
