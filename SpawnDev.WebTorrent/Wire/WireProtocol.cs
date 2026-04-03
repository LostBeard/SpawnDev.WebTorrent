namespace SpawnDev.WebTorrent.Wire;

/// <summary>
/// BitTorrent wire protocol message types.
/// See BEP 3: https://www.bittorrent.org/beps/bep_0003.html
/// </summary>
public enum MessageType : byte
{
    Choke = 0,
    Unchoke = 1,
    Interested = 2,
    NotInterested = 3,
    Have = 4,
    Bitfield = 5,
    Request = 6,
    Piece = 7,
    Cancel = 8,
    // BEP 5: DHT Port
    Port = 9,
    // BEP 6: Fast Extension
    SuggestPiece = 13,
    HaveAll = 14,
    HaveNone = 15,
    RejectRequest = 16,
    AllowedFast = 17,
    // BEP 10: Extension Protocol
    Extended = 20,
}

/// <summary>
/// BitTorrent wire protocol handler. Reads/writes protocol messages
/// over an IConnection. Handles handshake, message framing, and extensions.
/// </summary>
public class WireProtocol : IAsyncDisposable
{
    private readonly Transports.IConnection _connection;

    /// <summary>Remote peer's info hash (from handshake).</summary>
    public byte[]? RemoteInfoHash { get; private set; }

    /// <summary>Remote peer's peer ID (from handshake).</summary>
    public byte[]? RemotePeerId { get; private set; }

    /// <summary>Remote peer's extension flags (reserved bytes).</summary>
    public byte[]? RemoteReserved { get; private set; }

    /// <summary>Maximum allowed message size (16MB). Prevents OOM from malicious length prefixes.</summary>
    public const int MaxMessageSize = 16 * 1024 * 1024;

    /// <summary>Diagnostic log event.</summary>
    public event Action<string>? OnLog;

    /// <summary>Whether the remote peer supports BEP 10 (Extension Protocol).</summary>
    public bool SupportsExtensions => RemoteReserved != null && (RemoteReserved[5] & 0x10) != 0;

    /// <summary>Whether the remote peer supports BEP 6 (Fast Extension).</summary>
    public bool SupportsFastExtension => RemoteReserved != null && (RemoteReserved[7] & 0x04) != 0;

    // State
    public bool AmChoking { get; set; } = true;
    public bool AmInterested { get; set; } = false;
    public bool PeerChoking { get; set; } = true;
    public bool PeerInterested { get; set; } = false;

    // Events
    public event Action? OnChoke;
    public event Action? OnUnchoke;
    public event Action? OnInterested;
    public event Action? OnNotInterested;
    public event Action<int>? OnHave;
    public event Action<byte[]>? OnBitfield;
    public event Action<int, int, int>? OnRequest;      // pieceIndex, offset, length
    public event Action<int, int, byte[]>? OnPiece;      // pieceIndex, offset, data
    public event Action<int, int, int>? OnCancel;        // pieceIndex, offset, length
    public event Action<int, byte[]>? OnExtended;        // extensionId, payload
    public event Action? OnKeepAlive;
    public event Action? OnHandshakeComplete;
    // BEP 6: Fast Extension
    public event Action<int>? OnSuggestPiece;           // pieceIndex
    public event Action? OnHaveAll;
    public event Action? OnHaveNone;
    public event Action<int, int, int>? OnRejectRequest; // pieceIndex, offset, length
    public event Action<int>? OnAllowedFast;             // pieceIndex

    /// <summary>Protocol string for BitTorrent handshake.</summary>
    private static readonly byte[] ProtocolString = "BitTorrent protocol"u8.ToArray();

    public WireProtocol(Transports.IConnection connection)
    {
        _connection = connection;
        Extensions.Wire = this;
    }

    /// <summary>
    /// Send the BitTorrent handshake.
    /// Format: 1 byte (19) + "BitTorrent protocol" + 8 reserved bytes + 20 info_hash + 20 peer_id
    /// </summary>
    public async Task SendHandshakeAsync(byte[] infoHash, byte[] peerId, byte[]? reserved = null)
    {
        if (infoHash.Length != 20) throw new ArgumentException("Info hash must be 20 bytes");
        if (peerId.Length != 20) throw new ArgumentException("Peer ID must be 20 bytes");
        if (reserved != null && reserved.Length != 8) throw new ArgumentException("Reserved must be exactly 8 bytes");

        reserved ??= new byte[8];
        // Set BEP 10 (Extension Protocol) support flag
        reserved[5] |= 0x10;
        // Set BEP 6 (Fast Extension) support flag
        reserved[7] |= 0x04;

        var handshake = new byte[68];
        handshake[0] = 19; // protocol string length
        Array.Copy(ProtocolString, 0, handshake, 1, 19);
        Array.Copy(reserved, 0, handshake, 20, 8);
        Array.Copy(infoHash, 0, handshake, 28, 20);
        Array.Copy(peerId, 0, handshake, 48, 20);

        await _connection.SendAsync(handshake);
    }

    /// <summary>
    /// Receive and parse the BitTorrent handshake.
    /// </summary>
    public async Task<bool> ReceiveHandshakeAsync(CancellationToken ct = default)
    {
        var buf = new byte[68];
        int read = 0;
        while (read < 68)
        {
            int n = await _connection.ReceiveAsync(buf.AsMemory(read, 68 - read), ct);
            if (n <= 0) return false;
            read += n;
        }

        if (buf[0] != 19) return false;
        if (!buf.AsSpan(1, 19).SequenceEqual(ProtocolString)) return false;

        RemoteReserved = buf[20..28];
        RemoteInfoHash = buf[28..48];
        RemotePeerId = buf[48..68];
        OnHandshakeComplete?.Invoke();
        return true;
    }

    /// <summary>Send a simple message (no payload).</summary>
    public Task SendMessageAsync(MessageType type)
        => SendFramedAsync(new[] { (byte)type });

    /// <summary>Send a Have message (4-byte piece index).</summary>
    public Task SendHaveAsync(int pieceIndex)
    {
        var payload = new byte[5];
        payload[0] = (byte)MessageType.Have;
        WriteInt32BE(payload, 1, pieceIndex);
        return SendFramedAsync(payload);
    }

    /// <summary>Send a Request message.</summary>
    public Task SendRequestAsync(int pieceIndex, int offset, int length)
    {
        var payload = new byte[13];
        payload[0] = (byte)MessageType.Request;
        WriteInt32BE(payload, 1, pieceIndex);
        WriteInt32BE(payload, 5, offset);
        WriteInt32BE(payload, 9, length);
        return SendFramedAsync(payload);
    }

    /// <summary>Send a Cancel message (same format as Request: index + begin + length).</summary>
    public Task SendCancelAsync(int pieceIndex, int offset, int length)
    {
        var payload = new byte[13];
        payload[0] = (byte)MessageType.Cancel;
        WriteInt32BE(payload, 1, pieceIndex);
        WriteInt32BE(payload, 5, offset);
        WriteInt32BE(payload, 9, length);
        return SendFramedAsync(payload);
    }

    /// <summary>Send a Piece message (block data).</summary>
    public Task SendPieceAsync(int pieceIndex, int offset, byte[] data)
    {
        var payload = new byte[9 + data.Length];
        payload[0] = (byte)MessageType.Piece;
        WriteInt32BE(payload, 1, pieceIndex);
        WriteInt32BE(payload, 5, offset);
        Array.Copy(data, 0, payload, 9, data.Length);
        return SendFramedAsync(payload);
    }

    // ── BEP 6: Fast Extension ──

    /// <summary>Send a HaveAll message (we have every piece).</summary>
    public Task SendHaveAllAsync() => SendFramedAsync(new[] { (byte)MessageType.HaveAll });

    /// <summary>Send a HaveNone message (we have no pieces).</summary>
    public Task SendHaveNoneAsync() => SendFramedAsync(new[] { (byte)MessageType.HaveNone });

    /// <summary>Send a SuggestPiece message.</summary>
    public Task SendSuggestPieceAsync(int pieceIndex)
    {
        var payload = new byte[5];
        payload[0] = (byte)MessageType.SuggestPiece;
        WriteInt32BE(payload, 1, pieceIndex);
        return SendFramedAsync(payload);
    }

    /// <summary>Send a RejectRequest message (refuse a block request).</summary>
    public Task SendRejectRequestAsync(int pieceIndex, int offset, int length)
    {
        var payload = new byte[13];
        payload[0] = (byte)MessageType.RejectRequest;
        WriteInt32BE(payload, 1, pieceIndex);
        WriteInt32BE(payload, 5, offset);
        WriteInt32BE(payload, 9, length);
        return SendFramedAsync(payload);
    }

    /// <summary>Send an AllowedFast message.</summary>
    public Task SendAllowedFastAsync(int pieceIndex)
    {
        var payload = new byte[5];
        payload[0] = (byte)MessageType.AllowedFast;
        WriteInt32BE(payload, 1, pieceIndex);
        return SendFramedAsync(payload);
    }

    /// <summary>Send a Bitfield message.</summary>
    public Task SendBitfieldAsync(byte[] bitfield)
    {
        var payload = new byte[1 + bitfield.Length];
        payload[0] = (byte)MessageType.Bitfield;
        Array.Copy(bitfield, 0, payload, 1, bitfield.Length);
        return SendFramedAsync(payload);
    }

    /// <summary>Send a framed message (4-byte big-endian length prefix + payload).</summary>
    private async Task SendFramedAsync(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        WriteInt32BE(frame, 0, payload.Length);
        Array.Copy(payload, 0, frame, 4, payload.Length);
        await _connection.SendAsync(frame);
    }

    /// <summary>Send a BEP 10 extension message (msgId=20, extId, payload).</summary>
    public Task SendExtensionMessageAsync(int extensionId, byte[] payload)
    {
        var msg = new byte[2 + payload.Length];
        msg[0] = 20; // BEP 10 extended message
        msg[1] = (byte)extensionId;
        Array.Copy(payload, 0, msg, 2, payload.Length);
        return SendFramedAsync(msg);
    }

    /// <summary>Send a keep-alive (4 zero bytes).</summary>
    public Task SendKeepAliveAsync()
        => _connection.SendAsync(new byte[4]);

    /// <summary>Extension manager for BEP 10 protocol extensions.</summary>
    public ExtensionManager Extensions { get; } = new();

    /// <summary>
    /// Start the message read loop. Reads framed messages and dispatches events.
    /// Call after handshake is complete. Runs until connection closes.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        while (_connection.IsConnected && !ct.IsCancellationRequested)
        {
            // Read 4-byte length prefix
            int read = 0;
            while (read < 4)
            {
                int n = await _connection.ReceiveAsync(lenBuf.AsMemory(read, 4 - read), ct);
                if (n <= 0) return;
                read += n;
            }

            int msgLen = ReadInt32BE(lenBuf, 0);
            if (msgLen == 0) { OnKeepAlive?.Invoke(); continue; }
            if (msgLen < 0 || msgLen > MaxMessageSize)
            {
                OnLog?.Invoke($"Message too large: {msgLen} bytes (max {MaxMessageSize})");
                return; // Disconnect from malicious peer
            }

            var payload = new byte[msgLen];
            read = 0;
            while (read < msgLen)
            {
                int n = await _connection.ReceiveAsync(payload.AsMemory(read, msgLen - read), ct);
                if (n <= 0) return;
                read += n;
            }

            var type = (MessageType)payload[0];
            switch (type)
            {
                case MessageType.Choke: PeerChoking = true; OnChoke?.Invoke(); break;
                case MessageType.Unchoke: PeerChoking = false; OnUnchoke?.Invoke(); break;
                case MessageType.Interested: PeerInterested = true; OnInterested?.Invoke(); break;
                case MessageType.NotInterested: PeerInterested = false; OnNotInterested?.Invoke(); break;
                case MessageType.Have when payload.Length >= 5:
                    OnHave?.Invoke(ReadInt32BE(payload, 1)); break;
                case MessageType.Bitfield:
                    OnBitfield?.Invoke(payload[1..]); break;
                case MessageType.Request when payload.Length >= 13:
                    OnRequest?.Invoke(ReadInt32BE(payload, 1), ReadInt32BE(payload, 5), ReadInt32BE(payload, 9)); break;
                case MessageType.Piece when payload.Length >= 9:
                    OnPiece?.Invoke(ReadInt32BE(payload, 1), ReadInt32BE(payload, 5), payload[9..]); break;
                case MessageType.Cancel when payload.Length >= 13:
                    OnCancel?.Invoke(ReadInt32BE(payload, 1), ReadInt32BE(payload, 5), ReadInt32BE(payload, 9)); break;
                // BEP 6: Fast Extension — only process if remote peer advertised support
                case MessageType.SuggestPiece when SupportsFastExtension && payload.Length >= 5:
                    OnSuggestPiece?.Invoke(ReadInt32BE(payload, 1)); break;
                case MessageType.HaveAll when SupportsFastExtension:
                    OnHaveAll?.Invoke(); break;
                case MessageType.HaveNone when SupportsFastExtension:
                    OnHaveNone?.Invoke(); break;
                case MessageType.RejectRequest when payload.Length >= 13:
                    OnRejectRequest?.Invoke(ReadInt32BE(payload, 1), ReadInt32BE(payload, 5), ReadInt32BE(payload, 9)); break;
                case MessageType.AllowedFast when payload.Length >= 5:
                    OnAllowedFast?.Invoke(ReadInt32BE(payload, 1)); break;
                case MessageType.Extended when payload.Length >= 2:
                    int extId = payload[1];
                    var extPayload = payload.Length > 2 ? payload[2..] : Array.Empty<byte>();
                    OnExtended?.Invoke(extId, extPayload);
                    await Extensions.HandleMessageAsync(extId, extPayload);
                    break;
            }
        }
    }

    // ── Helpers ──

    private static void WriteInt32BE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static int ReadInt32BE(byte[] buf, int offset)
        => (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];

    public async ValueTask DisposeAsync()
    {
        await _connection.CloseAsync();
    }
}
