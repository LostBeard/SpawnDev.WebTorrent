using System.Net;
using System.Net.Sockets;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Accepts inbound BitTorrent peer-wire connections on a TCP port and routes
/// each connection to the matching torrent in a <see cref="WebTorrentClient"/>.
///
/// Why this exists: <see cref="TcpPeer.ConnectAsync"/> only handles the
/// outbound case (we initiate to a known peer). For full BEP 3 interoperability
/// with mainline clients (qBittorrent, Transmission, libtorrent) we also need to
/// accept inbound connections - a remote peer that learned our address from a
/// tracker / DHT / PEX will dial us first. Closes the seed-C# / leech-qBittorrent
/// path in the live-swarm interop matrix.
///
/// Routing strategy: peek the first 68 bytes of the BitTorrent handshake using
/// <see cref="SocketFlags.Peek"/> (kernel-buffer non-destructive read), parse
/// out the info_hash field (offset 28..47), look up the matching torrent in the
/// client. If found, hand the still-unconsumed <see cref="TcpClient"/> to a new
/// <see cref="TcpPeer"/> in responder mode and the wire reads the handshake
/// fresh from the stream. If no match (or malformed handshake), close the
/// socket - we don't try to negotiate without knowing which torrent.
///
/// Desktop only: browser has no TCP listener primitive.
/// </summary>
public sealed class TcpListenerService : IAsyncDisposable
{
    /// <summary>Length of the BitTorrent peer-wire handshake (BEP 3 §"Peer wire protocol"):
    /// 1-byte protocol-string length (0x13) + 19-byte protocol stamp + 8-byte reserved
    /// + 20-byte info_hash + 20-byte peer_id.</summary>
    private const int HandshakeLength = 68;

    private static readonly byte[] ProtocolStamp =
    {
        0x13, // protocol string length = 19
        (byte)'B', (byte)'i', (byte)'t', (byte)'T', (byte)'o', (byte)'r', (byte)'r', (byte)'e', (byte)'n', (byte)'t',
        (byte)' ',
        (byte)'p', (byte)'r', (byte)'o', (byte)'t', (byte)'o', (byte)'c', (byte)'o', (byte)'l',
    };

    private readonly WebTorrentClient _client;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>The local endpoint the listener is bound to. Useful when port=0
    /// was passed and the kernel picked a free port - inspect this to advertise
    /// the actual port number.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Total inbound connections accepted (for diagnostics).</summary>
    public int AcceptedCount { get; private set; }

    /// <summary>Connections that were closed because the handshake didn't match
    /// any torrent in the client (info_hash unknown, or malformed protocol stamp).</summary>
    public int RejectedCount { get; private set; }

    /// <summary>Optional per-event callback (logging, diagnostics).</summary>
    public event Action<string>? OnLog;

    /// <summary>Construct a listener bound to the given local endpoint.
    /// Use <see cref="StartAsync"/> to actually start accepting.</summary>
    public TcpListenerService(WebTorrentClient client, IPAddress address, int port)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _listener = new TcpListener(address, port);
    }

    /// <summary>Bind and start the accept loop. Returns once the socket is
    /// listening (bind/listen errors propagate); the accept loop runs in the
    /// background until <see cref="DisposeAsync"/>.</summary>
    public Task StartAsync()
    {
        _listener.Start();
        OnLog?.Invoke($"[TcpListener] listening on {LocalEndPoint}");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient accepted;
            try
            {
                accepted = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { return; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[TcpListener] accept failed: {ex.Message}");
                continue;
            }

            // Process each peer's handshake on a thread so a slow / malicious
            // peer can't stall the accept loop. Don't await - fire-and-forget by
            // design; HandleAcceptedAsync owns the socket and will dispose it on
            // any failure.
            _ = Task.Run(() => HandleAcceptedAsync(accepted, ct));
        }
    }

    private async Task HandleAcceptedAsync(TcpClient accepted, CancellationToken ct)
    {
        try
        {
            var remote = accepted.Client.RemoteEndPoint?.ToString() ?? "?";

            // Cap the time we'll wait for the peer's handshake. A real peer
            // sends 68 bytes immediately on connect; if we don't see them within
            // a few seconds it's a probe / stuck connection and we drop.
            using var peekCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            peekCts.CancelAfter(TimeSpan.FromSeconds(10));

            byte[] peeked;
            try
            {
                peeked = await PeekHandshakeAsync(accepted, peekCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[TcpListener] peek failed from {remote}: {ex.Message}");
                accepted.Dispose();
                return;
            }

            // Validate the protocol stamp before doing anything with the bytes.
            // Anything that doesn't lead with 0x13 + "BitTorrent protocol" isn't
            // a peer-wire connection - could be a port scan, an HTTP probe, etc.
            if (!HandshakeStartsWithProtocolStamp(peeked))
            {
                OnLog?.Invoke($"[TcpListener] non-BitTorrent handshake from {remote} - dropping");
                RejectedCount++;
                accepted.Dispose();
                return;
            }

            // info_hash is 20 bytes at offset 28 (1 + 19 + 8 = 28).
            var infoHash = new byte[20];
            Buffer.BlockCopy(peeked, 28, infoHash, 0, 20);
            var infoHashHex = Convert.ToHexString(infoHash).ToLowerInvariant();

            // Match by WireInfoHashHex so pure-v2 torrents (which use the first
            // 20 bytes of the v2 SHA-256 on the wire per libtorrent / qBittorrent
            // convention) get routed correctly alongside v1 + hybrid torrents.
            var torrent = _client.Torrents.FirstOrDefault(t =>
                string.Equals(t.WireInfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase));

            if (torrent == null)
            {
                OnLog?.Invoke($"[TcpListener] no torrent for info_hash {infoHashHex} from {remote} - dropping");
                RejectedCount++;
                accepted.Dispose();
                return;
            }

            OnLog?.Invoke($"[TcpListener] inbound peer {remote} matched torrent {infoHashHex[..16]}...");
            AcceptedCount++;

            // Hand the still-unconsumed socket off to a TcpPeer in responder mode.
            // Wire (created inside Torrent.AddPeer via Peer.CreateWebRTCPeer's TCP
            // path) parses the handshake fresh from the stream - the bytes are
            // still in the kernel buffer because we used MSG_PEEK above.
            //
            // AttachAsync configures the peer + fires EmitConnect synchronously
            // but does NOT start reading; AddPeer runs synchronously and wires
            // up OnData -> Wire.DataReceived; only THEN do we kick off the
            // read loop. Order matters: NetworkStream.ReadAsync resolves inline
            // when bytes are already kernel-buffered, so any read started before
            // AddPeer would race the OnData subscription and drop the remote
            // peer's BT handshake on the floor.
            var tcpPeer = new TcpPeer(initiator: false);
            await tcpPeer.AttachAsync(accepted, ct).ConfigureAwait(false);
            torrent.AddPeer(tcpPeer);
            tcpPeer.StartReadLoop();
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[TcpListener] handler exception: {ex.GetType().Name}: {ex.Message}");
            try { accepted.Dispose(); } catch { }
        }
    }

    /// <summary>Non-destructive read of the BT handshake (MSG_PEEK). Loops because
    /// kernel buffers may deliver the 68 bytes in chunks.</summary>
    private static async Task<byte[]> PeekHandshakeAsync(TcpClient client, CancellationToken ct)
    {
        var buf = new byte[HandshakeLength];
        var sock = client.Client;

        // Wait until the kernel buffer holds at least 68 bytes, then peek. Looping
        // an async wait without busy-spinning means we use SelectAsync via a small
        // delay each iteration; the alternative (Receive(MSG_PEEK | MSG_WAITALL))
        // isn't reliably implemented across .NET runtimes.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            int available = sock.Available;
            if (available >= HandshakeLength)
            {
                int peeked = sock.Receive(buf, 0, HandshakeLength, SocketFlags.Peek);
                if (peeked == HandshakeLength) return buf;
                throw new IOException($"Short peek: got {peeked} of {HandshakeLength}");
            }
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException("Timed out waiting for full BT handshake");
    }

    private static bool HandshakeStartsWithProtocolStamp(byte[] handshake)
    {
        if (handshake.Length < ProtocolStamp.Length) return false;
        for (int i = 0; i < ProtocolStamp.Length; i++)
            if (handshake[i] != ProtocolStamp[i]) return false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}
