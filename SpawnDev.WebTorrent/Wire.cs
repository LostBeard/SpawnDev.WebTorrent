using SpawnDev.WebTorrent.Bencode;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent;

/// <summary>
/// BitTorrent wire protocol implementation.
/// Direct 1:1 port of bittorrent-protocol/index.js from JS WebTorrent.
/// Handles message framing, handshake, BEP 6 Fast Extension, BEP 10 Extension Protocol,
/// keep-alive, request timeout, and speed tracking.
/// </summary>
public class Wire : IAsyncDisposable
{
    // ========================
    // CONSTANTS (match JS exactly)
    // ========================
    public const int BitfieldGrow = 400_000;
    public const int KeepAliveTimeout = 55_000;
    public const int AllowedFastSetMaxLength = 100;

    private static readonly byte[] MessageProtocol = { 19, (byte)'B', (byte)'i', (byte)'t', (byte)'T', (byte)'o', (byte)'r', (byte)'r', (byte)'e', (byte)'n', (byte)'t', (byte)' ', (byte)'p', (byte)'r', (byte)'o', (byte)'t', (byte)'o', (byte)'c', (byte)'o', (byte)'l' };
    private static readonly byte[] MessageKeepAlive = { 0, 0, 0, 0 };
    private static readonly byte[] MessageChoke = { 0, 0, 0, 1, 0 };
    private static readonly byte[] MessageUnchoke = { 0, 0, 0, 1, 1 };
    private static readonly byte[] MessageInterested = { 0, 0, 0, 1, 2 };
    private static readonly byte[] MessageUninterested = { 0, 0, 0, 1, 3 };
    private static readonly byte[] MessageHaveAll = { 0, 0, 0, 1, 0x0E };
    private static readonly byte[] MessageHaveNone = { 0, 0, 0, 1, 0x0F };
    private static readonly byte[] MessageReserved = { 0, 0, 0, 0, 0, 0, 0, 0 };

    // ========================
    // PUBLIC STATE (match JS wire properties)
    // ========================

    /// <summary>Remote peer ID as hex string.</summary>
    public string? PeerId { get; set; }

    /// <summary>Remote peer ID as bytes.</summary>
    public byte[]? PeerIdBuffer { get; set; }

    /// <summary>Connection type: webrtc, tcpIncoming, tcpOutgoing, webSeed.</summary>
    public string? Type { get; set; }

    /// <summary>Remote peer address (IP:port for TCP, ICE candidate address for WebRTC, URL for web seeds).</summary>
    public string? RemoteAddress { get; set; }

    /// <summary>Are WE choking the peer?</summary>
    public bool AmChoking { get; set; } = true;

    /// <summary>Are WE interested in the peer?</summary>
    public bool AmInterested { get; set; }

    /// <summary>Is the PEER choking us?</summary>
    public bool PeerChoking { get; set; } = true;

    /// <summary>Is the PEER interested in us?</summary>
    public bool PeerInterested { get; set; }

    /// <summary>Bitfield of pieces the peer has. True = peer has piece.</summary>
    public bool[]? PeerPieces { get; set; }

    /// <summary>Whether peer has ALL pieces (HaveAll received).</summary>
    public bool PeerHasAll { get; set; }

    /// <summary>Our extension support flags.</summary>
    public WireExtensions Extensions { get; set; } = new();

    /// <summary>Peer's extension support flags.</summary>
    public WireExtensions PeerExtensions { get; set; } = new();

    /// <summary>Outgoing requests (blocks we want from the peer). Thread-safe for web seed concurrency.</summary>
    public SynchronizedList<WireRequest> Requests { get; } = new();

    /// <summary>Incoming requests (blocks the peer wants from us).</summary>
    public SynchronizedList<WireRequest> PeerRequests { get; } = new();

    /// <summary>Our extension ID → name mapping.</summary>
    public Dictionary<int, string> ExtendedMapping { get; } = new();

    /// <summary>Peer's extension name → ID mapping.</summary>
    public Dictionary<string, int> PeerExtendedMapping { get; } = new();

    /// <summary>Our extended handshake data (minus 'm' field).</summary>
    public Dictionary<string, object> ExtendedHandshake { get; set; } = new();

    /// <summary>Peer's extended handshake data.</summary>
    public Dictionary<string, object>? PeerExtendedHandshake { get; set; }

    /// <summary>BEP 6: is Fast Extension enabled?</summary>
    public bool HasFast { get; set; }

    /// <summary>Pieces we allow fast requests for.</summary>
    public List<int> AllowedFastSet { get; } = new();

    /// <summary>Pieces peer allows fast requests for.</summary>
    public List<int> PeerAllowedFastSet { get; } = new();

    /// <summary>Total bytes uploaded to this peer.</summary>
    public long Uploaded { get; set; }

    /// <summary>Total bytes downloaded from this peer.</summary>
    public long Downloaded { get; set; }

    /// <summary>Is the wire destroyed/finished?</summary>
    public bool Destroyed { get; private set; }

    /// <summary>
    /// Back-reference to the underlying transport peer (set by <see cref="Peer.OnConnected"/>
    /// when the wire is created). <c>null</c> for web-seed wires (HTTP) and other transports
    /// without a SimplePeer-shaped backend. Consumers can read <see cref="SimplePeer.IsTransportDead"/>
    /// to detect phantom-alive wires whose <see cref="Destroyed"/> flag has not yet been set
    /// because the transport's close-event chain did not propagate.
    /// </summary>
    public SimplePeer? SimplePeer { get; internal set; }

    // Speed tracking - exponential moving average for smooth display
    internal long _downloadedSinceLastCheck;
    internal long _uploadedSinceLastCheck;
    private DateTime _lastSpeedCheck = DateTime.UtcNow;
    private double _smoothedDownSpeed;
    private double _smoothedUpSpeed;
    private const double SpeedAlpha = 0.3; // EMA smoothing factor (0.3 = 30% new, 70% old)

    /// <summary>Current download speed in bytes/sec (smoothed EMA).</summary>
    public double DownloadSpeed()
    {
        var elapsed = (DateTime.UtcNow - _lastSpeedCheck).TotalSeconds;
        if (elapsed < 0.5) return _smoothedDownSpeed;
        var instantSpeed = _downloadedSinceLastCheck / elapsed;
        _smoothedDownSpeed = _smoothedDownSpeed == 0
            ? instantSpeed
            : _smoothedDownSpeed * (1 - SpeedAlpha) + instantSpeed * SpeedAlpha;
        UpdateUploadSpeed(elapsed);
        _downloadedSinceLastCheck = 0;
        _uploadedSinceLastCheck = 0;
        _lastSpeedCheck = DateTime.UtcNow;
        return _smoothedDownSpeed;
    }

    /// <summary>Current upload speed in bytes/sec (smoothed EMA).</summary>
    public double UploadSpeed()
    {
        // DownloadSpeed() already resets the counters and updates the timestamp,
        // so UploadSpeed just returns the smoothed value
        return _smoothedUpSpeed;
    }

    // Called internally by DownloadSpeed to update both at once
    private void UpdateUploadSpeed(double elapsed)
    {
        var instantSpeed = _uploadedSinceLastCheck / elapsed;
        _smoothedUpSpeed = _smoothedUpSpeed == 0
            ? instantSpeed
            : _smoothedUpSpeed * (1 - SpeedAlpha) + instantSpeed * SpeedAlpha;
    }

    // ========================
    // EVENTS (match JS wire events)
    // ========================
    public event Action<string, string, WireExtensions>? OnHandshake; // infoHash, peerId, extensions
    public event Action? OnKeepAlive;
    public event Action? OnChoke;
    public event Action? OnUnchoke;
    public event Action? OnInterested;
    public event Action? OnUninterested;
    public event Action<int>? OnHave; // pieceIndex
    public event Action<byte[]>? OnBitfield; // raw bitfield bytes
    public event Action<int, int, int, Action<Exception?, byte[]?>>? OnRequest; // index, offset, length, respond
    public event Action<int, int, byte[]>? OnPiece; // index, offset, data
    public event Action<int, int, int>? OnCancel; // index, offset, length
    public event Action<int>? OnPort; // port
    public event Action<int>? OnSuggest; // pieceIndex
    public event Action? OnHaveAll;
    public event Action? OnHaveNone;
    public event Action<int, int, int>? OnReject; // index, offset, length
    public event Action<int>? OnAllowedFast; // pieceIndex
    /// <summary>BEP 52 v2 hash_request (peer msg id 21): remote asks for a range of Merkle tree hashes for a file.</summary>
    public event Action<Bep52WireMessages.HashRequest>? OnHashRequest;
    /// <summary>BEP 52 v2 hashes (peer msg id 22): remote delivers a Merkle-proof hash range.</summary>
    public event Action<Bep52WireMessages.Hashes>? OnHashes;
    /// <summary>BEP 52 v2 hash_reject (peer msg id 23): remote refuses a hash_request.</summary>
    public event Action<Bep52WireMessages.HashReject>? OnHashReject;
    public event Action<string, byte[]>? OnExtended; // ext name or "handshake", payload
    public event Action<int>? OnDownload; // bytes
    public event Action<int>? OnUpload; // bytes
    public event Action? OnTimeout;
    public event Action? OnClose;
    public event Action<byte[]>? OnUnknownMessage;

    // ========================
    // EXTENSION SYSTEM
    // ========================
    private readonly Dictionary<string, IWireExtension> _ext = new();
    private int _nextExt = 1;

    /// <summary>Get a registered extension by name. Returns null if not found.</summary>
    public IWireExtension? GetExtension(string name) => _ext.GetValueOrDefault(name);

    /// <summary>Get a registered extension by type. Returns null if not found.</summary>
    public T? GetExtension<T>() where T : class, IWireExtension
        => _ext.Values.OfType<T>().FirstOrDefault();

    /// <summary>Register a protocol extension (BEP 10). Must be called before handshake.</summary>
    public void Use(IWireExtension extension)
    {
        var name = extension.Name;
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Extension requires a Name property");

        ExtendedMapping[_nextExt] = name;
        _ext[name] = extension;
        _nextExt++;
    }

    // ========================
    // TRANSPORT ABSTRACTION
    // ========================

    /// <summary>Function to send raw bytes to the remote peer.</summary>
    public Func<byte[], Task>? SendRaw { get; set; }

    /// <summary>Call this when data arrives from the remote peer.</summary>
    public void DataReceived(byte[] data)
    {
        if (Destroyed) return;
        _bufferSize += data.Length;
        _buffer.AddRange(data);
        _processBuffer();
    }

    // ========================
    // INTERNAL PARSER STATE
    // ========================
    private int _parserSize;
    private Action<byte[]>? _parser;
    private readonly List<byte> _buffer = new();
    private int _bufferSize;
    private bool _finished;
    private bool _handshakeSent;
    private bool _extendedHandshakeSent;
    private byte[]? _infoHash;

    // Serializes outbound writes. NetworkStream / DataChannel sends MUST NOT
    // interleave - byte-level corruption results when two _push calls race
    // (e.g. four parallel SendPiece responses to a pipelined leecher's
    // request messages). The race surfaced in the qBittorrent reverse-direction
    // live-swarm test (C# seed -> qBT leech): four concurrent piece responses
    // got serialized at the byte stream and qBT received scrambled blocks,
    // failed hash verification, dropped the connection. Forward path never hit
    // it because leechers only send tiny request/interested/cancel messages,
    // not concurrent payloads.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Keep-alive
    private Timer? _keepAliveTimer;

    // Request timeout
    private Timer? _timeoutTimer;
    private int _timeoutMs;
    private DateTime? _timeoutExpiresAt;

    // ========================
    // CONSTRUCTOR
    // ========================

    public Wire(string? type = null)
    {
        Type = type;
        // Start parsing: expect handshake
        _parseHandshake();
    }

    // ========================
    // OUTGOING MESSAGES
    // ========================

    /// <summary>Send keep-alive: &lt;len=0000&gt;</summary>
    public async Task KeepAlive()
    {
        await _push(MessageKeepAlive);
    }

    /// <summary>Send choke: &lt;len=0001&gt;&lt;id=0&gt;</summary>
    public async Task Choke()
    {
        if (AmChoking) return;
        AmChoking = true;
        await _push(MessageChoke);

        if (HasFast)
        {
            // BEP6: reject all pending requests except allowed fast set
            int i = 0;
            while (i < PeerRequests.Count)
            {
                var req = PeerRequests[i];
                if (AllowedFastSet.Contains(req.Piece))
                    i++;
                else
                    await Reject(req.Piece, req.Offset, req.Length);
            }
        }
        else
        {
            PeerRequests.Clear();
        }
    }

    /// <summary>Send unchoke: &lt;len=0001&gt;&lt;id=1&gt;</summary>
    public async Task Unchoke()
    {
        if (!AmChoking) return;
        AmChoking = false;
        await _push(MessageUnchoke);
    }

    /// <summary>Send interested: &lt;len=0001&gt;&lt;id=2&gt;</summary>
    public async Task Interested()
    {
        if (AmInterested) return;
        AmInterested = true;
        await _push(MessageInterested);
    }

    /// <summary>Send uninterested: &lt;len=0001&gt;&lt;id=3&gt;</summary>
    public async Task Uninterested()
    {
        if (!AmInterested) return;
        AmInterested = false;
        await _push(MessageUninterested);
    }

    /// <summary>Send have: &lt;len=0005&gt;&lt;id=4&gt;&lt;piece index&gt;</summary>
    public Task Have(int index) => _message(4, new[] { index }, null);

    /// <summary>Send bitfield: &lt;len=0001+X&gt;&lt;id=5&gt;&lt;bitfield&gt;</summary>
    public Task Bitfield(byte[] bitfield) => _message(5, Array.Empty<int>(), bitfield);

    /// <summary>Send request: &lt;len=0013&gt;&lt;id=6&gt;&lt;index&gt;&lt;begin&gt;&lt;length&gt;</summary>
    public Task Request(int index, int offset, int length, Action<Exception?, byte[]?>? cb = null)
    {
        cb ??= (_, _) => { };
        if (_finished) { cb(new Exception("wire is closed"), null); return Task.CompletedTask; }

        if (PeerChoking && !(HasFast && PeerAllowedFastSet.Contains(index)))
        {
            cb(new Exception("peer is choking"), null);
            return Task.CompletedTask;
        }

        Requests.Add(new WireRequest(index, offset, length, cb));
        if (_timeoutTimer == null)
            _resetTimeout(true);
        return _message(6, new[] { index, offset, length }, null);
    }

    /// <summary>Send piece: &lt;len=0009+X&gt;&lt;id=7&gt;&lt;index&gt;&lt;begin&gt;&lt;block&gt;</summary>
    public async Task SendPiece(int index, int offset, byte[] buffer)
    {
        await _message(7, new[] { index, offset }, buffer);
        Uploaded += buffer.Length;
        _uploadedSinceLastCheck += buffer.Length;
        OnUpload?.Invoke(buffer.Length);
    }

    /// <summary>Send cancel: &lt;len=0013&gt;&lt;id=8&gt;&lt;index&gt;&lt;begin&gt;&lt;length&gt;</summary>
    public async Task Cancel(int index, int offset, int length)
    {
        var req = _pull(Requests, index, offset, length);
        req?.Callback(new Exception("request was cancelled"), null);
        await _message(8, new[] { index, offset, length }, null);
    }

    /// <summary>Send port: &lt;len=0003&gt;&lt;id=9&gt;&lt;listen-port&gt;</summary>
    public async Task Port(int port)
    {
        var msg = new byte[] { 0, 0, 0, 3, 9, (byte)(port >> 8), (byte)(port & 0xFF) };
        await _push(msg);
    }

    /// <summary>BEP6: Send suggest. No-op if peer doesn't support Fast Extension.</summary>
    public Task Suggest(int index) => HasFast ? _message(0x0D, new[] { index }, null) : Task.CompletedTask;

    /// <summary>BEP6: Send have-all. No-op if peer doesn't support Fast Extension.</summary>
    public Task HaveAll_Send() => HasFast ? _push(MessageHaveAll) : Task.CompletedTask;

    /// <summary>BEP6: Send have-none. No-op if peer doesn't support Fast Extension.</summary>
    public Task HaveNone_Send() => HasFast ? _push(MessageHaveNone) : Task.CompletedTask;

    /// <summary>BEP6: Send reject. No-op if peer doesn't support Fast Extension.</summary>
    public async Task Reject(int index, int offset, int length)
    {
        if (!HasFast) return;
        _pull(PeerRequests, index, offset, length);
        await _message(0x10, new[] { index, offset, length }, null);
    }

    /// <summary>BEP6: Send allowed-fast. No-op if peer doesn't support Fast Extension.</summary>
    public Task AllowedFast_Send(int index)
    {
        if (!HasFast) return Task.CompletedTask;
        if (!AllowedFastSet.Contains(index)) AllowedFastSet.Add(index);
        return _message(0x11, new[] { index }, null);
    }

    /// <summary>Send extended message (BEP 10).</summary>
    public Task Extended(int extId, byte[] payload)
    {
        var extIdByte = new byte[] { (byte)extId };
        var combined = new byte[extIdByte.Length + payload.Length];
        extIdByte.CopyTo(combined, 0);
        payload.CopyTo(combined, 1);
        return _message(20, Array.Empty<int>(), combined);
    }

    /// <summary>Send extended message by name.</summary>
    public Task Extended(string extName, byte[] payload)
    {
        if (!PeerExtendedMapping.TryGetValue(extName, out var extId))
            throw new Exception($"Unrecognized extension: {extName}");
        return Extended(extId, payload);
    }

    // ========================
    // HANDSHAKE
    // ========================

    /// <summary>Send BitTorrent handshake.</summary>
    /// <summary>
    /// Send BEP 52 v2 hash_request (peer msg id 21): ask the remote peer for a range of Merkle
    /// tree hashes so we can verify a piece we're downloading. Payload is exactly 48 bytes per
    /// spec - 32-byte pieces root + 4x u32 big-endian (base_layer / index / length / proof_layers).
    /// </summary>
    public Task SendHashRequest(Bep52WireMessages.HashRequest msg) =>
        _sendBep52(Bep52WireMessages.MessageIdHashRequest, Bep52WireMessages.Encode(msg));

    /// <summary>
    /// Send BEP 52 v2 hashes (peer msg id 22): deliver a Merkle-proof hash range to a peer that
    /// previously sent us a hash_request.
    /// </summary>
    public Task SendHashes(Bep52WireMessages.Hashes msg) =>
        _sendBep52(Bep52WireMessages.MessageIdHashes, Bep52WireMessages.Encode(msg));

    /// <summary>
    /// Send BEP 52 v2 hash_reject (peer msg id 23): refuse a peer's hash_request. Same 48-byte
    /// payload shape as hash_request (echoes the request fields).
    /// </summary>
    public Task SendHashReject(Bep52WireMessages.HashReject msg) =>
        _sendBep52(Bep52WireMessages.MessageIdHashReject, Bep52WireMessages.Encode(msg));

    private async Task _sendBep52(byte messageId, byte[] payload)
    {
        // Standard BT peer-message frame: [4B BE total-length] [1B msg id] [payload].
        // total-length = 1 (id byte) + payload.Length.
        var frame = new byte[4 + 1 + payload.Length];
        int len = 1 + payload.Length;
        frame[0] = (byte)((len >> 24) & 0xFF);
        frame[1] = (byte)((len >> 16) & 0xFF);
        frame[2] = (byte)((len >> 8) & 0xFF);
        frame[3] = (byte)(len & 0xFF);
        frame[4] = messageId;
        Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
        await _push(frame);
    }

    public async Task Handshake(byte[] infoHash, byte[] peerId, bool dht = false, bool fast = false)
    {
        if (infoHash.Length != 20 || peerId.Length != 20)
            throw new ArgumentException("infoHash and peerId MUST have length 20");

        _infoHash = infoHash;
        Extensions = new WireExtensions { Extended = true, Dht = dht, Fast = fast };

        var reserved = new byte[8];
        reserved[5] |= 0x10; // enable extended message (BEP 10)
        if (dht) reserved[7] |= 0x01;
        if (fast) reserved[7] |= 0x04;

        if (Extensions.Fast && PeerExtensions.Fast)
            HasFast = true;

        var msg = new byte[MessageProtocol.Length + reserved.Length + infoHash.Length + peerId.Length];
        int pos = 0;
        MessageProtocol.CopyTo(msg, pos); pos += MessageProtocol.Length;
        reserved.CopyTo(msg, pos); pos += reserved.Length;
        infoHash.CopyTo(msg, pos); pos += infoHash.Length;
        peerId.CopyTo(msg, pos);

        await _push(msg);
        _handshakeSent = true;

        if (PeerExtensions.Extended && !_extendedHandshakeSent)
            await _sendExtendedHandshake();
    }

    /// <summary>Set keep-alive on/off.</summary>
    public void SetKeepAlive(bool enable)
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
        if (!enable) return;
        _keepAliveTimer = new Timer(async _ => await KeepAlive(), null, KeepAliveTimeout, KeepAliveTimeout);
    }

    /// <summary>Set request timeout in ms.</summary>
    public void SetTimeout(int ms)
    {
        _timeoutMs = ms;
        _resetTimeout(true);
    }

    // ========================
    // INCOMING MESSAGE HANDLERS
    // ========================

    private void _handleChoke()
    {
        PeerChoking = true;
        OnChoke?.Invoke();
        if (!HasFast)
        {
            while (Requests.Count > 0)
            {
                var req = Requests[^1];
                Requests.RemoveAt(Requests.Count - 1);
                req.Callback(new Exception("peer is choking"), null);
            }
        }
    }

    private void _handleUnchoke()
    {
        PeerChoking = false;
        OnUnchoke?.Invoke();
    }

    private void _handleInterested()
    {
        PeerInterested = true;
        OnInterested?.Invoke();
    }

    private void _handleUninterested()
    {
        PeerInterested = false;
        OnUninterested?.Invoke();
    }

    private void _handleHave(int index)
    {
        if (PeerHasAll) return; // already has all
        if (PeerPieces != null && index < PeerPieces.Length && PeerPieces[index]) return;
        _ensurePeerPieces(index + 1);
        PeerPieces![index] = true;
        OnHave?.Invoke(index);
    }

    private void _handleBitfield(byte[] buffer)
    {
        int numPieces = buffer.Length * 8;
        PeerPieces = new bool[numPieces];
        for (int i = 0; i < buffer.Length; i++)
            for (int bit = 0; bit < 8; bit++)
                PeerPieces[i * 8 + bit] = (buffer[i] & (1 << (7 - bit))) != 0;
        PeerHasAll = false;
        OnBitfield?.Invoke(buffer);
    }

    private void _handleRequest(int index, int offset, int length)
    {
        if (AmChoking && !(HasFast && AllowedFastSet.Contains(index)))
        {
            if (HasFast) _ = Reject(index, offset, length);
            return;
        }

        var req = new WireRequest(index, offset, length, async (err, buf) =>
        {
            if (_pull(PeerRequests, index, offset, length) == null) return;
            if (err != null)
            {
                if (HasFast) await Reject(index, offset, length);
                return;
            }
            if (buf != null) await SendPiece(index, offset, buf);
        });
        PeerRequests.Add(req);
        OnRequest?.Invoke(index, offset, length, (err, buf) => req.Callback(err, buf));
    }

    private void _handlePiece(int index, int offset, byte[] buffer)
    {
        var req = _pull(Requests, index, offset, buffer.Length);
        if (req != null)
        {
            _resetTimeout(!PeerChoking && !_finished);
            req.Callback(null, buffer);
        }
        Downloaded += buffer.Length;
        _downloadedSinceLastCheck += buffer.Length;
        OnDownload?.Invoke(buffer.Length);
        OnPiece?.Invoke(index, offset, buffer);
    }

    private void _handleCancel(int index, int offset, int length)
    {
        _pull(PeerRequests, index, offset, length);
        OnCancel?.Invoke(index, offset, length);
    }

    private void _handlePort(int port) => OnPort?.Invoke(port);

    private void _handleSuggest(int index)
    {
        if (!HasFast) { Destroy(); return; }
        OnSuggest?.Invoke(index);
    }

    private void _handleHaveAll()
    {
        if (!HasFast) { Destroy(); return; }
        PeerHasAll = true;
        PeerPieces = null; // HaveAll means all pieces — don't store bitfield
        OnHaveAll?.Invoke();
    }

    private void _handleHaveNone()
    {
        if (!HasFast) { Destroy(); return; }
        OnHaveNone?.Invoke();
    }

    private void _handleReject(int index, int offset, int length)
    {
        if (!HasFast) { Destroy(); return; }
        var req = _pull(Requests, index, offset, length);
        req?.Callback(new Exception("request was rejected"), null);
        _resetTimeout(Requests.Count > 0);
        OnReject?.Invoke(index, offset, length);
    }

    private void _handleAllowedFast(int index)
    {
        if (!HasFast) { Destroy(); return; }
        if (!PeerAllowedFastSet.Contains(index)) PeerAllowedFastSet.Add(index);
        if (PeerAllowedFastSet.Count > AllowedFastSetMaxLength) PeerAllowedFastSet.RemoveAt(0);
        OnAllowedFast?.Invoke(index);
    }

    // BEP 52 v2 peer-message handlers. Each decodes the payload via Bep52WireMessages and
    // fires the corresponding event; if the payload is malformed the decoder throws an
    // ArgumentException which we convert into a quiet drop (log + ignore) rather than a
    // connection teardown, matching how the existing handlers tolerate wire-level noise.
    private void _handleHashRequest(byte[] payload)
    {
        try { OnHashRequest?.Invoke(Bep52WireMessages.DecodeHashRequest(payload)); }
        catch (ArgumentException) { OnUnknownMessage?.Invoke(payload); }
    }

    private void _handleHashes(byte[] payload)
    {
        try { OnHashes?.Invoke(Bep52WireMessages.DecodeHashes(payload)); }
        catch (ArgumentException) { OnUnknownMessage?.Invoke(payload); }
    }

    private void _handleHashReject(byte[] payload)
    {
        try { OnHashReject?.Invoke(Bep52WireMessages.DecodeHashReject(payload)); }
        catch (ArgumentException) { OnUnknownMessage?.Invoke(payload); }
    }

    private void _handleExtended(int extId, byte[] buf)
    {
        if (extId == 0)
        {
            // Extended handshake
            Dictionary<string, object>? info;
            try { var (dict, _) = BencodeDecoder.DecodeDictionary(buf, 0); info = dict; }
            catch { return; }
            if (info == null) return;

            PeerExtendedHandshake = info;

            if (info.TryGetValue("m", out var mObj) && mObj is Dictionary<string, object> m)
            {
                foreach (var (name, val) in m)
                {
                    if (val is long num)
                        PeerExtendedMapping[name] = (int)num;
                    else if (val is int inum)
                        PeerExtendedMapping[name] = inum;
                }
            }

            // Notify registered extensions
            foreach (var (name, ext) in _ext)
            {
                if (PeerExtendedMapping.ContainsKey(name))
                    ext.OnExtendedHandshake(PeerExtendedHandshake);
            }

            OnExtended?.Invoke("handshake", buf);
        }
        else
        {
            // Route to registered extension
            if (ExtendedMapping.TryGetValue(extId, out var extName))
            {
                if (_ext.TryGetValue(extName, out var handler))
                    handler.OnMessage(buf);
            }
            OnExtended?.Invoke(ExtendedMapping.GetValueOrDefault(extId, extId.ToString()), buf);
        }
    }

    private void _handleTimeout()
    {
        if (Requests.Count > 0)
        {
            var req = Requests[0];
            Requests.RemoveAt(0);
            req.Callback(new Exception("request has timed out"), null);
        }
        // Re-arm timer if there are more pending requests
        _resetTimeout(Requests.Count > 0);
        OnTimeout?.Invoke();
    }

    // ========================
    // PARSER (state machine matching JS _write + _parse)
    // ========================

    private void _parseHandshake()
    {
        _parse(1, buffer =>
        {
            int pstrlen = buffer[0];
            if (pstrlen != 19)
            {
                Destroy();
                return;
            }
            _parse(pstrlen + 48, _onHandshakeBuffer);
        });
    }

    private void _onHandshakeBuffer(byte[] handshake)
    {
        var protocol = Encoding.ASCII.GetString(handshake, 0, 19);
        if (protocol != "BitTorrent protocol")
        {
            Destroy();
            return;
        }

        var reserved = handshake[19..27];
        var infoHashBuf = handshake[27..47];
        var peerIdBuf = handshake[47..67];

        var extensions = new WireExtensions
        {
            Dht = (reserved[7] & 0x01) != 0,
            Fast = (reserved[7] & 0x04) != 0,
            Extended = (reserved[5] & 0x10) != 0,
        };

        PeerId = Convert.ToHexString(peerIdBuf).ToLowerInvariant();
        PeerIdBuffer = peerIdBuf;
        PeerExtensions = extensions;

        if (Extensions.Fast && PeerExtensions.Fast)
            HasFast = true;

        OnHandshake?.Invoke(
            Convert.ToHexString(infoHashBuf).ToLowerInvariant(),
            PeerId,
            extensions
        );

        // Notify registered extensions
        foreach (var (_, ext) in _ext)
            ext.OnHandshake(Convert.ToHexString(infoHashBuf).ToLowerInvariant(), PeerId, extensions);

        if (extensions.Extended && _handshakeSent && !_extendedHandshakeSent)
            _ = _sendExtendedHandshake();

        // Now parse messages
        _parse(4, _onMessageLength);
    }

    private void _onMessageLength(byte[] buffer)
    {
        int length = ReadInt32BE(buffer, 0);
        if (length > 0)
            _parse(length, _onMessage);
        else
        {
            OnKeepAlive?.Invoke();
            _parse(4, _onMessageLength);
        }
    }

    private void _onMessage(byte[] buffer)
    {
        _parse(4, _onMessageLength);
        switch (buffer[0])
        {
            case 0: _handleChoke(); break;
            case 1: _handleUnchoke(); break;
            case 2: _handleInterested(); break;
            case 3: _handleUninterested(); break;
            case 4: _handleHave(ReadInt32BE(buffer, 1)); break;
            case 5: _handleBitfield(buffer[1..]); break;
            case 6: _handleRequest(ReadInt32BE(buffer, 1), ReadInt32BE(buffer, 5), ReadInt32BE(buffer, 9)); break;
            case 7: _handlePiece(ReadInt32BE(buffer, 1), ReadInt32BE(buffer, 5), buffer[9..]); break;
            case 8: _handleCancel(ReadInt32BE(buffer, 1), ReadInt32BE(buffer, 5), ReadInt32BE(buffer, 9)); break;
            case 9: _handlePort((buffer[1] << 8) | buffer[2]); break;
            case 0x0D: _handleSuggest(ReadInt32BE(buffer, 1)); break;
            case 0x0E: _handleHaveAll(); break;
            case 0x0F: _handleHaveNone(); break;
            case 0x10: _handleReject(ReadInt32BE(buffer, 1), ReadInt32BE(buffer, 5), ReadInt32BE(buffer, 9)); break;
            case 0x11: _handleAllowedFast(ReadInt32BE(buffer, 1)); break;
            case 20: _handleExtended(buffer[1], buffer[2..]); break;
            // BEP 52 v2 peer messages (core, not BEP 10 extensions). Spec §"Protocol extension".
            case Bep52WireMessages.MessageIdHashRequest: _handleHashRequest(buffer[1..]); break;
            case Bep52WireMessages.MessageIdHashes: _handleHashes(buffer[1..]); break;
            case Bep52WireMessages.MessageIdHashReject: _handleHashReject(buffer[1..]); break;
            default: OnUnknownMessage?.Invoke(buffer); break;
        }
    }

    // ========================
    // PARSER INFRASTRUCTURE
    // ========================

    private void _parse(int size, Action<byte[]> parser)
    {
        _parserSize = size;
        _parser = parser;
    }

    private void _processBuffer()
    {
        while (_bufferSize >= _parserSize && _parser != null)
        {
            if (_parserSize == 0)
            {
                _parser(Array.Empty<byte>());
            }
            else
            {
                var data = _buffer.GetRange(0, _parserSize).ToArray();
                _buffer.RemoveRange(0, _parserSize);
                _bufferSize -= _parserSize;
                _parser(data);
            }
        }
    }

    // ========================
    // INTERNAL HELPERS
    // ========================

    private async Task _push(byte[] data)
    {
        if (_finished || SendRaw == null) return;
        // Serialize via _sendLock - see _sendLock declaration for the rationale.
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await SendRaw(data).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task _message(int id, int[] numbers, byte[]? data)
    {
        int dataLength = data?.Length ?? 0;
        int headerLen = 5 + (4 * numbers.Length);
        // Build the full frame in one buffer so concurrent _message calls don't
        // interleave header and payload bytes between two _push acquisitions of
        // the send lock. Pre-fix this method did `await _push(header); await
        // _push(data);` - two separate lock-protected sends - which let two
        // concurrent SendPiece calls write `header_A | header_B | data_A | data_B`
        // and produce a corrupt byte stream that qBittorrent then rejected on
        // hash verification, dropping the connection after one good block.
        // Caught by the qbittorrent_reverse_liveswarm.cs interop test.
        var frame = new byte[headerLen + dataLength];
        WriteInt32BE(frame, 0, headerLen + dataLength - 4);
        frame[4] = (byte)id;
        for (int i = 0; i < numbers.Length; i++)
            WriteInt32BE(frame, 5 + (4 * i), numbers[i]);
        if (data != null && data.Length > 0)
            Buffer.BlockCopy(data, 0, frame, headerLen, data.Length);
        await _push(frame);
    }

    private async Task _sendExtendedHandshake()
    {
        var msg = new Dictionary<string, object>(ExtendedHandshake);
        var m = new Dictionary<string, object>();
        foreach (var (extId, name) in ExtendedMapping)
            m[name] = extId;
        msg["m"] = m;

        var encoded = BencodeEncoder.Encode(msg);
        await Extended(0, encoded);
        _extendedHandshakeSent = true;
    }

    private void _resetTimeout(bool setAgain)
    {
        if (!setAgain || _timeoutMs == 0 || Requests.Count == 0)
        {
            _timeoutTimer?.Dispose();
            _timeoutTimer = null;
            _timeoutExpiresAt = null;
            return;
        }

        var expiresAt = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
        if (_timeoutTimer != null && _timeoutExpiresAt.HasValue)
        {
            if ((expiresAt - _timeoutExpiresAt.Value).TotalMilliseconds < _timeoutMs * 0.05)
                return;
        }

        _timeoutTimer?.Dispose();
        _timeoutExpiresAt = expiresAt;
        _timeoutTimer = new Timer(_ => _handleTimeout(), null, _timeoutMs, Timeout.Infinite);
    }

    private WireRequest? _pull(SynchronizedList<WireRequest> requests, int piece, int offset, int length)
    {
        var match = requests.FirstOrDefault(r => r.Piece == piece && r.Offset == offset && r.Length == length);
        if (match != null) requests.Remove(match);
        return match;
    }

    private void _ensurePeerPieces(int minLength)
    {
        if (PeerPieces == null || PeerPieces.Length < minLength)
        {
            var newPieces = new bool[Math.Min(Math.Max(minLength, (PeerPieces?.Length ?? 0) * 2), BitfieldGrow)];
            PeerPieces?.CopyTo(newPieces, 0);
            PeerPieces = newPieces;
        }
    }

    /// <summary>Check if peer has a specific piece.</summary>
    public bool PeerHasPiece(int index)
    {
        if (PeerHasAll) return true;
        if (PeerPieces == null || index >= PeerPieces.Length) return false;
        return PeerPieces[index];
    }

    public void Destroy()
    {
        if (Destroyed) return;
        Destroyed = true;
        _finished = true;
        _keepAliveTimer?.Dispose();
        _timeoutTimer?.Dispose();
        PeerRequests.Clear();
        while (Requests.Count > 0)
        {
            var req = Requests[^1];
            Requests.RemoveAt(Requests.Count - 1);
            req.Callback(new Exception("wire was closed"), null);
        }
        OnClose?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        Destroy();
        return ValueTask.CompletedTask;
    }

    // ========================
    // BINARY HELPERS
    // ========================

    private static int ReadInt32BE(byte[] buf, int offset)
        => (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];

    private static void WriteInt32BE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)((value >> 24) & 0xFF);
        buf[offset + 1] = (byte)((value >> 16) & 0xFF);
        buf[offset + 2] = (byte)((value >> 8) & 0xFF);
        buf[offset + 3] = (byte)(value & 0xFF);
    }
}

// ========================
// SUPPORTING TYPES
// ========================

/// <summary>Wire extension support flags.</summary>
public class WireExtensions
{
    public bool Extended { get; set; }
    public bool Dht { get; set; }
    public bool Fast { get; set; }
}

/// <summary>A pending block request.</summary>
public class WireRequest
{
    public int Piece { get; }
    public int Offset { get; }
    public int Length { get; }
    public Action<Exception?, byte[]?> Callback { get; }

    public WireRequest(int piece, int offset, int length, Action<Exception?, byte[]?> callback)
    {
        Piece = piece;
        Offset = offset;
        Length = length;
        Callback = callback;
    }
}

/// <summary>Interface for BEP 10 wire extensions.</summary>
public interface IWireExtension
{
    string Name { get; }
    void OnHandshake(string infoHash, string peerId, WireExtensions extensions);
    void OnExtendedHandshake(Dictionary<string, object> handshake);
    void OnMessage(byte[] buf);
}
