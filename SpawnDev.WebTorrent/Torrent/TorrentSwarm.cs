using SpawnDev.BlazorJS;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent.Torrent;

/// <summary>
/// Manages a single torrent's swarm: peers, pieces, downloads, and uploads.
/// Coordinates piece selection, request scheduling, and choke/unchoke rotation.
/// </summary>
public class TorrentSwarm : IAsyncDisposable
{
    private readonly WebTorrentClient _client;
    private readonly AddTorrentOptions _options;
    private readonly List<PeerConnection> _peers = new();
    private readonly HashSet<string> _knownPeerAddresses = new();
    private readonly SemaphoreSlim _peerLock = new(1, 1);
    private IChunkStore? _store;
    private PieceManager? _pieceManager;
    private DownloadCoordinator? _coordinator;
    private PeerCoordinator? _peerCoordinator;
    private readonly List<Func<TorrentSwarm, Wire.WireProtocol, Wire.WireExtension>> _extensionFactories = new();
    private bool _disposed;

    /// <summary>Torrent metadata (available after initialization or metadata exchange).</summary>
    public TorrentMetadata? Metadata { get; private set; }

    /// <summary>20-byte info hash.</summary>
    public byte[] InfoHash { get; private set; } = Array.Empty<byte>();

    /// <summary>Info hash as lowercase hex string.</summary>
    public string InfoHashHex => Convert.ToHexString(InfoHash).ToLowerInvariant();

    /// <summary>Display name — metadata name, or short hash if no metadata yet.</summary>
    public string Name => Metadata?.Name ?? InfoHashHex[..Math.Min(16, InfoHashHex.Length)] + "...";

    /// <summary>Whether metadata has been received and parsed.</summary>
    public bool HasMetadata => Metadata != null;

    /// <summary>Whether all selected pieces have been downloaded and verified.</summary>
    public bool Done { get; private set; }

    /// <summary>Download progress (0.0 to 1.0).</summary>
    public double Progress => _pieceManager?.Progress ?? 0;

    /// <summary>Current download speed in bytes/sec.</summary>
    public double DownloadSpeed { get; private set; }

    /// <summary>Current upload speed in bytes/sec.</summary>
    public double UploadSpeed { get; private set; }

    /// <summary>Total bytes downloaded (verified).</summary>
    public long Downloaded { get; private set; }

    /// <summary>Total bytes uploaded.</summary>
    public long Uploaded { get; private set; }

    /// <summary>Tracker announce URLs from metadata.</summary>
    public string[] Announce => Metadata?.AnnounceList?.SelectMany(t => t).ToArray() ?? Array.Empty<string>();

    /// <summary>Total bytes received (including unverified). Used for bandwidth stats.</summary>
    public long Received { get; private set; }

    /// <summary>Number of web seed URLs configured.</summary>
    public int WebSeedCount => _coordinator?.WebSeedCount ?? 0;

    /// <summary>Connected peer count.</summary>
    public int PeerCount => _peers.Count;

    /// <summary>Connected peers (for UI display).</summary>
    public IReadOnlyList<PeerConnection> Peers => _peers;

    /// <summary>Bitfield of verified pieces.</summary>
    public bool[]? Bitfield => _pieceManager?.Bitfield;

    /// <summary>Files in this torrent (available after metadata).</summary>
    public TorrentFileStream[] Files { get; private set; } = Array.Empty<TorrentFileStream>();

    /// <summary>The piece manager (available after metadata is set).</summary>
    public PieceManager? PieceManager => _pieceManager;

    /// <summary>The download coordinator (available after metadata is set).</summary>
    public DownloadCoordinator? Coordinator => _coordinator;

    /// <summary>The chunk store (available after metadata is set).</summary>
    public IChunkStore? Store => _store;

    /// <summary>Whether the swarm is paused (not connecting to new peers).</summary>
    public bool Paused { get; private set; }

    /// <summary>Whether to download pieces sequentially (for streaming) or rarest-first.</summary>
    public bool Sequential
    {
        get => _coordinator?.Strategy == "sequential";
        set { if (_coordinator != null) _coordinator.Strategy = value ? "sequential" : "rarest"; OnStateChanged?.Invoke(); }
    }

    /// <summary>Selected file indices (null = all files selected).</summary>
    public int[]? SelectedFileIndices { get; set; }

    /// <summary>Per-torrent upload rate limit in bytes/sec (-1 = use client default).</summary>
    public long PerTorrentUploadLimit { get; set; } = -1;

    /// <summary>Per-torrent download rate limit in bytes/sec (-1 = use client default).</summary>
    public long PerTorrentDownloadLimit { get; set; } = -1;

    // Speed tracking
    private long _downloadedSinceLastTick;
    private long _uploadedSinceLastTick;
    private DateTime _lastSpeedTick = DateTime.UtcNow;

    /// <summary>Whether the torrent is ready (metadata available and store ready).</summary>
    public bool Ready => HasMetadata && _store != null;

    /// <summary>Magnet URI for this torrent.</summary>
    public string MagnetURI
    {
        get
        {
            var hash = Convert.ToHexString(InfoHash).ToLowerInvariant();
            var uri = $"magnet:?xt=urn:btih:{hash}";
            if (Metadata?.Name != null)
                uri += $"&dn={Uri.EscapeDataString(Metadata.Name)}";
            if (Metadata?.AnnounceList != null)
                foreach (var tier in Metadata.AnnounceList)
                    foreach (var tracker in tier)
                        uri += $"&tr={Uri.EscapeDataString(tracker)}";
            return uri;
        }
    }

    /// <summary>Export .torrent file bytes (null if no metadata).</summary>
    public byte[]? TorrentFileBytes => Metadata?.OriginalTorrentBytes;

    /// <summary>Seed ratio (uploaded / downloaded). 0 if nothing downloaded.</summary>
    public double Ratio => Downloaded > 0 ? (double)Uploaded / Downloaded : 0;

    /// <summary>Estimated time remaining in milliseconds. 0 if done, -1 if unknown.</summary>
    public long TimeRemaining
    {
        get
        {
            if (Done) return 0;
            if (Metadata == null || DownloadSpeed <= 0) return -1;
            long remaining = Metadata.TotalLength - Downloaded;
            return (long)(remaining / DownloadSpeed * 1000);
        }
    }

    /// <summary>Torrent creation date (from metadata).</summary>
    public DateTimeOffset? Created => Metadata?.CreationDate;

    /// <summary>Creator string (from metadata).</summary>
    public string? CreatedBy => Metadata?.CreatedBy;

    /// <summary>Comment (from metadata).</summary>
    public string? Comment => Metadata?.Comment;

    /// <summary>Whether this is a private torrent (BEP 27 — no DHT/PEX).</summary>
    public bool IsPrivate => Metadata?.IsPrivate ?? false;

    /// <summary>Standard piece length (except possibly last piece).</summary>
    public int PieceLength => Metadata?.PieceLength ?? 0;

    /// <summary>Length of the last piece.</summary>
    public int LastPieceLength => Metadata != null
        ? (int)(Metadata.TotalLength - (long)(Metadata.PieceCount - 1) * Metadata.PieceLength)
        : 0;

    /// <summary>Total torrent size in bytes.</summary>
    public long Length => Metadata?.TotalLength ?? 0;

    // Events
    public event Action? OnReady;
    public event Action? OnDone;
    public event Action<long>? OnDownload;
    public event Action<long>? OnUpload;
    public event Action<int>? OnPieceVerified;
    public event Action<PeerConnection>? OnPeerConnect;
    public event Action<PeerConnection>? OnPeerDisconnect;
    public event Action<Exception>? OnError;
    public event Action<string>? OnLog;
    public event Action? OnMetadata;
    public event Action<string>? OnWarning;

    public TorrentSwarm(WebTorrentClient client, AddTorrentOptions options)
    {
        _client = client;
        _options = options;
        Paused = options.Paused;

        // Register built-in BEP 10 extensions on every wire
        UseExtension((swarm, wire) =>
        {
            var ext = new Wire.UtMetadataExtension();
            // If we have metadata, serve it to peers
            if (swarm.Metadata?.InfoDictBytes != null)
                ext.LocalMetadata = swarm.Metadata.InfoDictBytes;
            ext.ExpectedInfoHash = swarm.InfoHash.Length > 0 ? swarm.InfoHash : null;
            // When metadata is received from a peer, set it on this swarm
            ext.OnMetadataComplete += async (infoDictBytes) =>
            {
                try
                {
                    var meta = TorrentParser.ParseInfoDict(infoDictBytes, swarm.InfoHash);
                    if (meta != null && swarm.Metadata == null)
                        await swarm.SetMetadataAsync(meta);
                }
                catch (Exception ex) { swarm.OnLog?.Invoke($"ut_metadata parse failed: {ex.Message}"); }
            };
            return ext;
        });
        UseExtension((swarm, wire) =>
        {
            var ext = new Wire.UtPexExtension();
            ext.OnPeersReceived += (peers) =>
            {
                if (swarm.IsPrivate) return; // BEP 27: no PEX for private torrents
                foreach (var addr in peers)
                    swarm.AddPeer(new PeerInfo { Address = addr, Source = "pex" });
            };
            return ext;
        });
    }

    /// <summary>Initialize from magnet URI or info hash string.</summary>
    public async Task InitializeAsync(string magnetOrInfoHash)
    {
        if (magnetOrInfoHash.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = TorrentParser.ParseMagnet(magnetOrInfoHash);
            InfoHash = parsed.InfoHash;

            // Merge magnet web seeds and trackers regardless of xs= path
            var magnetWebSeeds = parsed.UrlList;
            var magnetTrackers = parsed.AnnounceList.SelectMany(a => a).ToArray();

            // If magnet has xs= (exact source), fetch full .torrent metadata from it
            if (parsed.ExactSource != null)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    var torrentBytes = await http.GetByteArrayAsync(parsed.ExactSource);
                    var fullMetadata = TorrentParser.Parse(torrentBytes);
                    // Merge magnet web seeds into metadata (xs= .torrent may not have them)
                    if (magnetWebSeeds.Length > 0)
                        fullMetadata.UrlList = fullMetadata.UrlList.Concat(magnetWebSeeds).Distinct().ToArray();
                    await SetMetadataAsync(fullMetadata);
                    OnLog?.Invoke($"xs= metadata loaded: {fullMetadata.Name}, {fullMetadata.PieceCount} pieces");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"xs= fetch failed: {ex.Message} — will rely on peers for metadata");
                    // Fall through to tracker/peer path
                    _pendingUrlList = magnetWebSeeds;
                }
            }
            else
            {
                // Store web seeds for when metadata arrives via peers
                _pendingUrlList = magnetWebSeeds;
            }

            // Connect to trackers (needed for both xs= and peer paths)
            if (magnetTrackers.Length > 0)
                _ = ConnectToTrackersAsync(magnetTrackers);
        }
        else if (magnetOrInfoHash.Length == 40)
        {
            InfoHash = Convert.FromHexString(magnetOrInfoHash);
        }
        else
        {
            throw new ArgumentException("Expected magnet URI or 40-char hex info hash");
        }
    }

    private string[]? _pendingUrlList;
    private string[]? _pendingTrackers;

    /// <summary>Set metadata (from .torrent file parse or ut_metadata exchange).</summary>
    public async Task SetMetadataAsync(TorrentMetadata metadata)
    {
        if (Metadata != null) return; // already set

        Metadata = metadata;
        InfoHash = metadata.InfoHash;

        // Create chunk store — use custom factory, or platform default
        _store = _options.StoreFactory?.Invoke(metadata.PieceLength)
            ?? CreateDefaultStore(metadata);

        // Create piece manager
        _pieceManager = new PieceManager(metadata, _store, _client.Crypto);
        _pieceManager.OnPieceComplete += HandlePieceComplete;

        // Scan for already-downloaded pieces (restore from OPFS after reload)
        _ = ScanExistingPiecesAsync();

        // Create download coordinator
        _coordinator = new DownloadCoordinator(_pieceManager, metadata);
        _coordinator.OnDownloadComplete += () =>
        {
            Done = true;
            OnDone?.Invoke();
        };
        _coordinator.OnError += (ex) => OnError?.Invoke(ex);
        _coordinator.OnLog += (msg) => OnLog?.Invoke(msg);

        // Create file stream abstractions
        Files = metadata.Files.Select(f => new TorrentFileStream(this, f, _store)).ToArray();

        // Add web seeds from metadata + pending (from magnet ws= parameter)
        var allWebSeeds = metadata.UrlList.ToList();
        if (_pendingUrlList != null && _pendingUrlList.Length > 0)
        {
            allWebSeeds.AddRange(_pendingUrlList);
            _pendingUrlList = null;
        }
        foreach (var ws in allWebSeeds.Distinct())
            AddWebSeed(ws.TrimEnd('/'));

        // Add any already-connected peers to the coordinator.
        // If a peer sent HaveAll before metadata arrived, its bitfield is empty.
        // Now that we have metadata, fill it with all-true.
        foreach (var peer in _peers)
        {
            if (peer.PeerBitfield.Length == 0 && metadata.PieceCount > 0)
            {
                // Peer was connected before metadata — assume it has all pieces
                // (it sent HaveAll which we couldn't process without PieceCount)
                peer.PeerBitfield = new bool[metadata.PieceCount];
                Array.Fill(peer.PeerBitfield, true);
            }
            _coordinator.AddPeer(peer.Wire, peer.PeerBitfield);
        }

        // Start the download loop if not paused and not already done
        if (!Paused && !Done)
            _coordinator.Start();

        // Connect to trackers from metadata — must complete before OnReady fires
        await ConnectTrackersFromMetadataAsync();

        OnMetadata?.Invoke();
        OnReady?.Invoke();
    }

    /// <summary>
    /// Connect to all trackers listed in the torrent metadata.
    /// Creates PeerCoordinator + WebRtcTransport for WebSocket trackers.
    /// </summary>
    private async Task ConnectTrackersFromMetadataAsync()
    {
        if (Metadata == null) return;

        var trackerUrls = Metadata.AnnounceList.SelectMany(a => a).ToArray();
        if (trackerUrls.Length > 0)
            _ = ConnectToTrackersAsync(trackerUrls);
    }

    /// <summary>
    /// Connect to tracker URLs using just InfoHash (metadata not required).
    /// Called from both SetMetadata (has full metadata) and InitializeAsync (magnet with trackers).
    /// </summary>
    private async Task ConnectToTrackersAsync(string[] trackerUrls)
    {
        var wsTrackers = trackerUrls.Where(u => u.StartsWith("wss://") || u.StartsWith("ws://")).ToArray();
        var httpTrackers = trackerUrls.Where(u => u.StartsWith("http://") || u.StartsWith("https://")).ToArray();

        // WebSocket trackers — need WebRTC transport for P2P (browser + desktop)
        if (wsTrackers.Length > 0 && _peerCoordinator == null)
        {
            try
            {
                var webRtc = Transports.IWebRtcTransport.Create();
                var coordinator = new PeerCoordinator(_client, InfoHash, webRtc);
                coordinator.Swarm = this;
                _peerCoordinator = coordinator;
                // Apply any registered extension factories
                foreach (var factory in _extensionFactories)
                    coordinator.UseExtension(factory);
                coordinator.OnPeerConnected += async (peer) =>
                {
                    try
                    {
                        await AddConnectedPeerAsync(peer.Wire, new PeerInfo { Address = peer.PeerId, Source = "webrtc" });
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"AddConnectedPeer failed: {ex.Message}");
                    }
                };

                foreach (var url in wsTrackers)
                {
                    try
                    {
                        await coordinator.AddTrackerAsync(url);
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"Tracker {url} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"WebRTC tracker setup failed: {ex.Message}");
            }
        }

        // HTTP trackers — peer discovery (desktop)
        foreach (var url in httpTrackers)
        {
            try
            {
                var tracker = new Discovery.HttpTrackerClient(url, _client.PeerId);
                tracker.OnPeer += (peer) => AddPeer(peer);
                await tracker.StartAsync(InfoHash, 0);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"HTTP tracker {url} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Add a discovered peer and initiate connection.</summary>
    public void AddPeer(PeerInfo info)
    {
        if (Paused) return;
        if (_peers.Count >= 55) return;

        // Private torrents: only accept peers from trackers, not DHT/PEX
        if (IsPrivate && info.Source != "ws-tracker" && info.Source != "udp-tracker"
            && info.Source != "http-tracker" && info.Source != "manual")
        {
            return;
        }

        // Deduplicate
        if (!_knownPeerAddresses.Add(info.Address)) return;

        _ = ConnectToPeerAsync(info);
    }

    private async Task ConnectToPeerAsync(PeerInfo info)
    {
        try
        {
            var transport = FindTransport(info);
            if (transport == null) return;

            // Connection timeout: 25 seconds for WebRTC, 5 seconds for TCP
            using var cts = new CancellationTokenSource(25000);
            var conn = await transport.ConnectAsync(info.Address, cts.Token);

            // Perform BitTorrent handshake
            var wire = new WireProtocol(conn);
            // Register extensions before handshake for BEP 10 negotiation
            foreach (var factory in _extensionFactories)
                wire.Extensions.Register(factory(this, wire));
            await wire.SendHandshakeAsync(InfoHash, _client.PeerId);

            if (!await wire.ReceiveHandshakeAsync())
            {
                await conn.CloseAsync();
                return;
            }

            // Verify info hash matches
            if (wire.RemoteInfoHash == null || !wire.RemoteInfoHash.SequenceEqual(InfoHash))
            {
                await conn.CloseAsync();
                return;
            }

            // Send BEP 10 extended handshake if both sides support it
            if (wire.SupportsExtensions && wire.Extensions.Count > 0)
            {
                var extHandshake = wire.Extensions.BuildHandshake();
                var encoded = Bencode.BencodeEncoder.Encode(
                    extHandshake.ToDictionary(kv => kv.Key, kv => kv.Value));
                await wire.SendExtensionMessageAsync(0, encoded);
            }

            await AddConnectedPeerAsync(wire, info);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"Failed to connect to {info.Address}: {ex.Message}");
        }
    }

    /// <summary>Add a peer that has already completed the handshake (from PeerCoordinator or incoming).</summary>
    public async Task AddConnectedPeerAsync(WireProtocol wire, PeerInfo info)
    {
        if (_disposed)
        {
            await wire.DisposeAsync();
            return;
        }
        await _peerLock.WaitAsync();
        try
        {
            if (_peers.Count >= 55)
            {
                await wire.DisposeAsync();
                return;
            }

            var peer = new PeerConnection(wire, info);

            // Wire up events
            wire.OnBitfield += (bf) =>
            {
                peer.PeerBitfield = new bool[bf.Length * 8];
                for (int i = 0; i < bf.Length; i++)
                    for (int bit = 0; bit < 8; bit++)
                        if (i * 8 + bit < peer.PeerBitfield.Length)
                            peer.PeerBitfield[i * 8 + bit] = (bf[i] & (1 << (7 - bit))) != 0;
                int trueCount = peer.PeerBitfield.Count(b => b);

                // Add to coordinator if metadata is available
                _coordinator?.AddPeer(wire, peer.PeerBitfield);
            };

            // BEP 6: HaveAll — peer has every piece
            wire.OnHaveAll += () =>
            {
                if (Metadata != null)
                {
                    peer.PeerBitfield = new bool[Metadata.PieceCount];
                    Array.Fill(peer.PeerBitfield, true);
                    _coordinator?.AddPeer(wire, peer.PeerBitfield);
                }
            };

            // BEP 6: HaveNone — peer has no pieces
            wire.OnHaveNone += () =>
            {
                if (Metadata != null)
                    peer.PeerBitfield = new bool[Metadata.PieceCount];
            };

            wire.OnHave += (pieceIndex) =>
            {
                if (pieceIndex < peer.PeerBitfield.Length)
                    peer.PeerBitfield[pieceIndex] = true;
            };

            wire.OnRequest += async (pieceIndex, offset, length) =>
            {
                try
                {
                    // Seeding: respond to piece requests
                    if (_store != null && _pieceManager != null && _pieceManager.Bitfield[pieceIndex])
                    {
                        var data = await _store.GetAsync(pieceIndex, offset, length);
                        if (data != null)
                        {
                            // Apply upload rate limiting
                            await _client.UploadLimiter.WaitAsync(data.Length);
                            await wire.SendPieceAsync(pieceIndex, offset, data);
                            Uploaded += data.Length;
                            _uploadedSinceLastTick += data.Length;
                            peer.BytesUploaded += data.Length;
                            OnUpload?.Invoke(data.Length);
                        }
                    }
                }
                catch (Exception ex) { OnLog?.Invoke($"OnRequest handler failed: {ex.Message}"); }
            };

            _peers.Add(peer);
            OnPeerConnect?.Invoke(peer);

            // Register BEP 10 extensions if not already registered
            if (wire.Extensions.Count == 0 && _extensionFactories.Count > 0)
            {
                foreach (var factory in _extensionFactories)
                    wire.Extensions.Register(factory(this, wire));
            }

            // Send BEP 10 extended handshake if both sides support it
            if (wire.SupportsExtensions && wire.Extensions.Count > 0)
            {
                var extHandshake = wire.Extensions.BuildHandshake();
                var encoded = Bencode.BencodeEncoder.Encode(
                    extHandshake.ToDictionary(kv => kv.Key, kv => kv.Value));
                await wire.SendExtensionMessageAsync(0, encoded);
            }

            // Send interested + unchoke
            await wire.SendMessageAsync(MessageType.Interested);
            await wire.SendMessageAsync(MessageType.Unchoke);

            // Send our bitfield if we have metadata and any pieces
            bool hasPieces = _pieceManager != null && _pieceManager.Bitfield.Any(b => b);
            if (hasPieces)
            {
                await wire.SendBitfieldAsync(BoolBitfieldToBytes(_pieceManager.Bitfield));
            }

            // If we don't have metadata, request it AFTER RunAsync processes the
            // remote's BEP 10 handshake (which sets RemoteId and MetadataSize)
            if (Metadata == null)
            {
                wire.Extensions.OnRemoteHandshake += () =>
                {
                    var utMeta = wire.Extensions.Get<Wire.UtMetadataExtension>();
                    if (utMeta != null && utMeta.IsSupported && utMeta.MetadataSize > 0)
                        utMeta.RequestAllPieces();
                };
            }

            // Start message read loop — processes buffered BEP 10 handshake,
            // which triggers OnRemoteHandshake → ut_metadata request
            _ = RunPeerAsync(peer);
        }
        finally
        {
            _peerLock.Release();
        }
    }

    private async Task RunPeerAsync(PeerConnection peer)
    {
        using var keepAliveCts = new CancellationTokenSource();
        _ = KeepAliveLoopAsync(peer, keepAliveCts.Token);

        try
        {
            await peer.Wire.RunAsync();
        }
        catch { }
        finally
        {
            keepAliveCts.Cancel();
            try
            {
                await _peerLock.WaitAsync();
                try
                {
                    _peers.Remove(peer);
                    _knownPeerAddresses.Remove(peer.Info.Address);
                }
                finally
                {
                    _peerLock.Release();
                }
            }
            catch (ObjectDisposedException) { }
            OnPeerDisconnect?.Invoke(peer);
        }
    }

    private static async Task KeepAliveLoopAsync(PeerConnection peer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(60000, ct); // 60 seconds
                await peer.Wire.SendKeepAliveAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>Convert bool[] bitfield to packed byte[] for wire protocol.</summary>
    private static byte[] BoolBitfieldToBytes(bool[] bitfield)
    {
        int byteCount = (bitfield.Length + 7) / 8;
        var bytes = new byte[byteCount];
        for (int i = 0; i < bitfield.Length; i++)
            if (bitfield[i])
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
        return bytes;
    }

    private Transports.ITransport? FindTransport(PeerInfo info)
    {
        // For now, use the first available transport
        // TCP addresses look like "ip:port", WebRTC peer IDs are hex strings
        // The transport selection can be made smarter later
        return null; // Peers come through PeerCoordinator which handles transport
    }

    private void HandlePieceComplete(int pieceIndex)
    {
        if (_pieceManager == null || Metadata == null) return;

        int pieceLength = (pieceIndex == Metadata.PieceCount - 1)
            ? (int)(Metadata.TotalLength - (long)pieceIndex * Metadata.PieceLength)
            : Metadata.PieceLength;

        Downloaded += pieceLength;
        _downloadedSinceLastTick += pieceLength;
        OnPieceVerified?.Invoke(pieceIndex);
        OnDownload?.Invoke(pieceLength);

        // Notify all peers we have this piece
        foreach (var peer in _peers.ToArray())
        {
            try { _ = peer.Wire.SendHaveAsync(pieceIndex); }
            catch (Exception ex) { OnLog?.Invoke($"SendHave failed: {ex.Message}"); }
        }

        if (_pieceManager.IsComplete)
        {
            Done = true;
            OnDone?.Invoke();
        }
    }

    private IChunkStore CreateDefaultStore(TorrentMetadata metadata)
    {
        var hashHex = Convert.ToHexString(metadata.InfoHash).ToLowerInvariant();

        // Use AsyncFSChunkStore if an IAsyncFS is provided via options
        if (_options.AsyncFileSystem != null)
            return new AsyncFSChunkStore(_options.AsyncFileSystem, $"webtorrent/{hashHex}", metadata.PieceLength);

        // Desktop: use FileChunkStore in temp directory
        if (!OperatingSystem.IsBrowser())
        {
            var dir = Path.Combine(Path.GetTempPath(), "SpawnDev.WebTorrent", hashHex);
            return new FileChunkStore(dir, metadata.PieceLength);
        }

        // Browser fallback: memory
        return new MemoryChunkStore(metadata.PieceLength);
    }

    /// <summary>Scan chunk store for already-downloaded pieces and rebuild the bitfield.</summary>
    private async Task ScanExistingPiecesAsync()
    {
        if (_store == null || _pieceManager == null || Metadata == null) return;
        try
        {
            for (int i = 0; i < Metadata.PieceCount; i++)
            {
                try
                {
                    var piece = await _store.GetAsync(i);
                    if (piece != null && piece.Length > 0)
                    {
                        _pieceManager.MarkComplete(i);
                    }
                }
                catch
                {
                    // Stale/corrupt piece — remove it so it gets re-downloaded
                    try { await _store.RemoveAsync(i); } catch { }
                }
            }
            if (_pieceManager.IsComplete)
            {
                Done = true;
            }
        }
        catch
        {
            // Never crash the app from a restore scan failure
        }
    }

    /// <summary>Update speed calculations. Call periodically (e.g., every second).</summary>
    public void UpdateSpeed()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedTick).TotalSeconds;
        if (elapsed >= 0.5)
        {
            DownloadSpeed = _downloadedSinceLastTick / elapsed;
            UploadSpeed = _uploadedSinceLastTick / elapsed;
            _downloadedSinceLastTick = 0;
            _uploadedSinceLastTick = 0;
            _lastSpeedTick = now;
        }
    }

    /// <summary>Mark the swarm as fully downloaded (for seeding).</summary>
    internal void MarkDone()
    {
        Done = true;
        OnDone?.Invoke();
    }

    /// <summary>Start the download coordinator and choke rotation.</summary>
    public void StartDownload()
    {
        _coordinator?.Start();
        StartChokeRotation();
    }

    /// <summary>Stop the download coordinator and choke rotation.</summary>
    public void StopDownload()
    {
        _coordinator?.Stop();
        _chokeRotationCts?.Cancel();
    }

    private CancellationTokenSource? _chokeRotationCts;

    /// <summary>
    /// Choke/unchoke rotation (BEP 3).
    /// Every 10 seconds: unchoke the best uploading peers (up to 4).
    /// Every 30 seconds: optimistic unchoke one random choked peer.
    /// </summary>
    private void StartChokeRotation()
    {
        _chokeRotationCts = new CancellationTokenSource();
        _ = ChokeRotationLoopAsync(_chokeRotationCts.Token);
    }

    private async Task ChokeRotationLoopAsync(CancellationToken ct)
    {
        int tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(10000, ct); // 10 second rechoke interval
                tick++;

                var peers = _peers.ToArray();
                if (peers.Length == 0) continue;

                // BEP 3: Unchoke the 4 interested peers with highest upload rate to us
                var interested = peers.Where(p => p.IsInterested)
                    .OrderByDescending(p => p.UploadRate)
                    .ToArray();
                var notInterested = peers.Where(p => !p.IsInterested).ToArray();

                int unchokedCount = 0;
                foreach (var peer in interested)
                {
                    if (unchokedCount < 4)
                    {
                        try { await peer.Wire.SendMessageAsync(Wire.MessageType.Unchoke); }
                        catch { }
                        unchokedCount++;
                    }
                    else
                    {
                        try { await peer.Wire.SendMessageAsync(Wire.MessageType.Choke); }
                        catch { }
                    }
                }
                // Choke all uninterested peers
                foreach (var peer in notInterested)
                {
                    try { await peer.Wire.SendMessageAsync(Wire.MessageType.Choke); }
                    catch { }
                }

                // Reset upload counters for next interval
                foreach (var peer in peers) peer.ResetUploadCounter();

                // Every 30 seconds: optimistic unchoke a random choked interested peer
                if (tick % 3 == 0)
                {
                    var chokedInterested = interested.Skip(4).ToArray();
                    if (chokedInterested.Length > 0)
                    {
                        var lucky = chokedInterested[Random.Shared.Next(chokedInterested.Length)];
                        try { await lucky.Wire.SendMessageAsync(Wire.MessageType.Unchoke); }
                        catch { }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Pause — stop connecting to new peers.</summary>
    public void Pause()
    {
        Paused = true;
        _coordinator?.Stop();
        OnStateChanged?.Invoke();
    }

    /// <summary>Resume — allow connecting to new peers and restart download.</summary>
    public void Resume()
    {
        Paused = false;
        if (HasMetadata && _coordinator != null)
            _coordinator.Start();
        OnStateChanged?.Invoke();
    }

    /// <summary>Fired when paused/resumed or other operational state changes that should be persisted.</summary>
    public event Action? OnStateChanged;

    /// <summary>Prioritize a range of pieces.</summary>
    public void Select(int startPiece, int endPiece, int priority = 1)
    {
        if (_coordinator == null) return;
        for (int i = startPiece; i <= endPiece; i++)
            _coordinator.Prioritize(i);
    }

    /// <summary>Deprioritize a range of pieces.</summary>
    public void Deselect(int startPiece, int endPiece)
    {
        // Currently no explicit deprioritize in coordinator — just don't prioritize
    }

    /// <summary>Mark pieces as critical (highest priority, download ASAP).</summary>
    public void Critical(int startPiece, int endPiece)
    {
        Select(startPiece, endPiece, 10);
    }

    /// <summary>Remove a specific peer by address.</summary>
    public async Task RemovePeerAsync(string address)
    {
        await _peerLock.WaitAsync();
        try
        {
            var peer = _peers.FirstOrDefault(p => p.Info.Address == address);
            if (peer != null)
            {
                _peers.Remove(peer);
                _knownPeerAddresses.Remove(address);
                await peer.DisposeAsync();
                OnPeerDisconnect?.Invoke(peer);
            }
        }
        finally { _peerLock.Release(); }
    }

    /// <summary>Re-verify all pieces in the store against their hashes.</summary>
    public async Task RescanFilesAsync()
    {
        if (_pieceManager == null || _store == null || Metadata == null) return;

        for (int i = 0; i < Metadata.PieceCount; i++)
        {
            var data = await _store.GetAsync(i);
            if (data != null && Metadata.VerifyPiece(i, data))
            {
                _pieceManager.MarkComplete(i);
            }
        }
    }

    /// <summary>
    /// Register a wire extension factory. Extensions are created for every new peer
    /// BEFORE the BEP 10 handshake, so they participate in extension negotiation.
    /// Same pattern as JS WebTorrent's wire.use(extensionFactory).
    /// </summary>
    public void UseExtension(Func<TorrentSwarm, Wire.WireProtocol, Wire.WireExtension> factory)
    {
        _extensionFactories.Add(factory);
        _peerCoordinator?.UseExtension(factory);
    }

    /// <summary>Add a web seed URL.</summary>
    public void AddWebSeed(string url)
    {
        _coordinator?.AddWebSeed(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, url);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _coordinator?.Stop();

        // Dispose PeerCoordinator first — stops tracker clients so no new peers arrive
        if (_peerCoordinator != null)
            await _peerCoordinator.DisposeAsync();

        foreach (var peer in _peers.ToArray())
            await peer.DisposeAsync();
        _peers.Clear();

        if (_store != null)
            await _store.DisposeAsync();

        _peerLock.Dispose();
    }
}

/// <summary>
/// Wrapper around a peer connection with its wire protocol state.
/// </summary>
public class PeerConnection : IAsyncDisposable
{
    public WireProtocol Wire { get; }
    public PeerInfo Info { get; }
    public bool[] PeerBitfield { get; set; } = Array.Empty<bool>();
    public bool IsChoked { get; set; } = true;
    public bool IsInterested { get; set; }
    public long BytesUploaded { get; set; }
    private DateTime _lastUploadReset = DateTime.UtcNow;

    /// <summary>Upload rate in bytes/sec over the last choke interval.</summary>
    public double UploadRate
    {
        get
        {
            var elapsed = (DateTime.UtcNow - _lastUploadReset).TotalSeconds;
            return elapsed > 0 ? BytesUploaded / elapsed : 0;
        }
    }

    /// <summary>Reset upload rate counter (call at each choke interval).</summary>
    public void ResetUploadCounter()
    {
        BytesUploaded = 0;
        _lastUploadReset = DateTime.UtcNow;
    }

    public PeerConnection(WireProtocol wire, PeerInfo info)
    {
        Wire = wire;
        Info = info;

        wire.OnChoke += () => IsChoked = true;
        wire.OnUnchoke += () => IsChoked = false;
        wire.OnInterested += () => IsInterested = true;
        wire.OnNotInterested += () => IsInterested = false;
    }

    public async ValueTask DisposeAsync()
    {
        await Wire.DisposeAsync();
    }
}

/// <summary>
/// Random-access file stream over torrent pieces.
/// Supports byte-range reads for ML model weight streaming.
/// </summary>
public class TorrentFileStream
{
    private readonly TorrentSwarm _swarm;
    private readonly TorrentFile _file;
    private readonly IChunkStore _store;

    public string Name => _file.Name;
    public string Path => _file.Path;
    public long Length => _file.Length;
    public long Size => _file.Length;
    public long Offset => _file.Offset;
    public int StartPiece => _file.StartPiece;
    public int EndPiece => _file.EndPiece;

    /// <summary>Whether this file has been fully downloaded.</summary>
    public bool Done
    {
        get
        {
            var bitfield = _swarm.Bitfield;
            if (bitfield == null) return false;
            for (int i = _file.StartPiece; i <= _file.EndPiece; i++)
                if (!bitfield[i]) return false;
            return true;
        }
    }

    /// <summary>Bytes downloaded for this file.</summary>
    public long Downloaded
    {
        get
        {
            var bitfield = _swarm.Bitfield;
            if (bitfield == null || _swarm.Metadata == null) return 0;
            long bytes = 0;
            for (int i = _file.StartPiece; i <= _file.EndPiece; i++)
                if (bitfield[i])
                    bytes += (i == _swarm.Metadata.PieceCount - 1)
                        ? _swarm.Metadata.TotalLength - (long)i * _swarm.Metadata.PieceLength
                        : _swarm.Metadata.PieceLength;
            return Math.Min(bytes, _file.Length);
        }
    }

    /// <summary>MIME type based on file extension.</summary>
    public string Type => GetMimeType(System.IO.Path.GetExtension(Path).ToLowerInvariant());

    /// <summary>Select this file for download (prioritize its pieces).</summary>
    public void Select(int priority = 1)
    {
        _swarm.Select(_file.StartPiece, _file.EndPiece, priority);
    }

    /// <summary>Deselect this file (deprioritize its pieces).</summary>
    public void Deselect()
    {
        _swarm.Deselect(_file.StartPiece, _file.EndPiece);
    }

    /// <summary>Check if a piece index contains data from this file.</summary>
    public bool Includes(int pieceIndex) => pieceIndex >= _file.StartPiece && pieceIndex <= _file.EndPiece;

    /// <summary>
    /// Get the streaming URL for this file (served by the service worker).
    /// Point a video/audio/img element's src at this URL for streaming with seeking.
    /// Requires ServiceWorkerStreamHandler to be registered.
    /// </summary>
    public string StreamURL => ServiceWorkerStreamHandler.GetStreamUrl(_swarm, this);

    /// <summary>
    /// Set the src of an HTML media element to this file's streaming URL.
    /// Supports streaming, seeking, and all browser codecs.
    /// Requires ServiceWorkerStreamHandler to be registered.
    /// </summary>
    public void StreamTo(BlazorJS.JSObjects.HTMLMediaElement elem)
    {
        elem.Src = StreamURL;
    }

    /// <summary>Get the entire file as a byte array (blocks until complete).</summary>
    public Task<byte[]> GetArrayBufferAsync(CancellationToken ct = default)
        => ReadAsync(0, (int)Length, ct);

    // Events
    public event Action? OnDone;

    internal void CheckDone()
    {
        if (Done) OnDone?.Invoke();
    }

    /// <summary>Download progress for this specific file (0.0 to 1.0).</summary>
    public double Progress
    {
        get
        {
            var bitfield = _swarm.Bitfield;
            if (bitfield == null) return 0;
            int count = 0, total = _file.EndPiece - _file.StartPiece + 1;
            for (int i = _file.StartPiece; i <= _file.EndPiece; i++)
                if (bitfield[i]) count++;
            return total > 0 ? (double)count / total : 0;
        }
    }

    public TorrentFileStream(TorrentSwarm swarm, TorrentFile file, IChunkStore store)
    {
        _swarm = swarm;
        _file = file;
        _store = store;
    }

    /// <summary>
    /// Read bytes from this file at a specific offset.
    /// If the torrent is paused and the requested pieces aren't available,
    /// automatically resumes downloading for this file's pieces.
    /// This is the key API for ML model weight streaming and media playback.
    /// </summary>
    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
    {
        if (_swarm.Metadata == null) throw new InvalidOperationException("Metadata not available");
        var meta = _swarm.Metadata;

        var result = new byte[length];
        int resultOffset = 0;
        long fileAbsOffset = _file.Offset + offset;

        while (resultOffset < length)
        {
            int pieceIndex = (int)(fileAbsOffset / meta.PieceLength);
            int pieceOffset = (int)(fileAbsOffset % meta.PieceLength);
            int bytesInPiece = Math.Min(meta.PieceLength - pieceOffset, length - resultOffset);

            // Wait for this piece to be available
            var bitfield = _swarm.Bitfield;
            if (bitfield != null && !bitfield[pieceIndex])
            {
                // Auto-resume if paused — the stream accessor needs data
                if (_swarm.Paused)
                    _swarm.Resume();

                // Signal the coordinator to prioritize this piece
                _swarm.Coordinator?.Prioritize(pieceIndex);

                while (!bitfield[pieceIndex])
                {
                    await Task.Delay(10, ct);
                    bitfield = _swarm.Bitfield;
                    if (bitfield == null) break;
                }
            }

            var pieceData = await _store.GetAsync(pieceIndex, pieceOffset, bytesInPiece, ct);
            if (pieceData != null)
            {
                Array.Copy(pieceData, 0, result, resultOffset, bytesInPiece);
            }

            resultOffset += bytesInPiece;
            fileAbsOffset += bytesInPiece;
        }

        return result;
    }

    /// <summary>
    /// Get a seekable .NET Stream for this file. Pieces download on demand as the stream is read.
    /// Works on both desktop and browser. Use like any System.IO.Stream.
    /// </summary>
    public Stream CreateReadStream(long start = 0) => new TorrentReadStream(this, start);

    /// <summary>
    /// Get a ReadableStream for this file (browser). Reads on demand as the stream is consumed.
    /// </summary>
    public async IAsyncEnumerable<byte[]> StreamAsync(long start = 0, long end = -1,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (end < 0) end = Length - 1;
        int chunkSize = _swarm.Metadata?.PieceLength ?? 262144;
        long pos = start;

        while (pos <= end)
        {
            int readLen = (int)Math.Min(chunkSize, end - pos + 1);
            var data = await ReadAsync(pos, readLen, ct);
            yield return data;
            pos += readLen;
        }
    }

    /// <summary>
    /// Get the file as a Blob (browser). Blocks until fully downloaded.
    /// In browser, returns a JS Blob via SpawnDev.BlazorJS.
    /// On desktop, returns the raw bytes.
    /// </summary>
    public async Task<byte[]> GetBlobBytesAsync(CancellationToken ct = default)
        => await ReadAsync(0, (int)Length, ct);

    private static string GetMimeType(string ext) => ext switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".ogv" => "video/ogg",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".aac" or ".m4a" => "audio/aac",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".zip" => "application/zip",
        ".srt" => "text/plain",
        _ => "application/octet-stream",
    };
}
