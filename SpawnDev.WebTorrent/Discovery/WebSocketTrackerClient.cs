using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnDev.WebTorrent.Discovery;

/// <summary>
/// Convert bytes to binary string (latin1, one char per byte) for WebTorrent tracker protocol.
/// JS WebTorrent uses hex2bin() — NOT hex encoding.
/// </summary>
public static class TrackerEncoding
{
    /// <summary>Convert bytes to binary string (latin1). Each byte becomes one char.</summary>
    public static string ToBinaryString(byte[] bytes)
        => new string(bytes.Select(b => (char)b).ToArray());

    /// <summary>Convert binary string back to bytes.</summary>
    public static byte[] FromBinaryString(string binaryStr)
        => binaryStr.Select(c => (byte)c).ToArray();
}

/// <summary>
/// WebSocket tracker client compatible with WebTorrent tracker protocol.
/// Handles announce, offer/answer relay for WebRTC signaling.
///
/// Protocol: JSON messages over WebSocket.
/// - Client sends: { action: "announce", info_hash, peer_id, ... }
/// - Server responds: { action: "announce", peers: [...], complete, incomplete }
/// - Client sends: { action: "offer", to_peer_id, offer, offer_id }
/// - Server relays: { action: "offer", peer_id (from), offer, offer_id }
/// - Client sends: { action: "answer", to_peer_id, answer, offer_id }
/// - Server relays: { action: "answer", peer_id (from), answer, offer_id }
///
/// Works in both desktop (.NET ClientWebSocket) and browser (Blazor WASM WebSocket).
/// </summary>
public class WebSocketTrackerClient : IDiscovery
{
    private readonly string _trackerUrl;
    private readonly byte[] _peerId;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private byte[]? _currentInfoHash;
    private int _currentPort;
    private int _announceIntervalMs = 30_000;
    private Func<TrackerOffer[]?>? _offerFactory;

    public string Type => "ws-tracker";
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action<PeerInfo>? OnPeer;
    public event Action<int, int>? OnAnnounceResponse;
    public event Action<string, string, JsonElement>? OnOffer;
    public event Action<string, string, JsonElement>? OnAnswer;
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    public WebSocketTrackerClient(string trackerUrl, byte[] peerId)
    {
        _trackerUrl = trackerUrl;
        _peerId = peerId;
    }

    public async Task StartAsync(byte[] infoHash, int port, CancellationToken ct = default)
        => await StartAsync(infoHash, port, null, null, ct);

    public async Task StartAsync(byte[] infoHash, int port, TrackerOffer[]? offers,
        Func<TrackerOffer[]?>? offerFactory = null, CancellationToken ct = default)
    {
        _currentInfoHash = infoHash;
        _currentPort = port;
        _offerFactory = offerFactory;
        _ws = new ClientWebSocket();
        _readCts = new CancellationTokenSource();

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(15_000);
        await _ws.ConnectAsync(new Uri(_trackerUrl), connectCts.Token);
        OnConnected?.Invoke();
        _readLoop = ReadLoopAsync(_readCts.Token);

        try
        {
            await AnnounceAsync(infoHash, port, 0, 0, 0, offers, TrackerEvent.Started, ct);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"First announce failed: {ex.Message}");
        }

        _ = ReannounceLoopAsync(_readCts.Token);
    }

    public Task AnnounceAsync(byte[] infoHash, int port,
        long uploaded, long downloaded, long left,
        TrackerEvent trackerEvent = TrackerEvent.None, CancellationToken ct = default)
        => AnnounceAsync(infoHash, port, uploaded, downloaded, left, null, trackerEvent, ct);

    /// <summary>Announce with pre-generated WebRTC offers (WebTorrent protocol).</summary>
    public async Task AnnounceAsync(byte[] infoHash, int port,
        long uploaded, long downloaded, long left,
        TrackerOffer[]? offers, TrackerEvent trackerEvent = TrackerEvent.None,
        CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        string? eventStr = trackerEvent switch
        {
            TrackerEvent.Started => "started",
            TrackerEvent.Stopped => "stopped",
            TrackerEvent.Completed => "completed",
            TrackerEvent.None => null,
            _ => null,
        };

        if (offers != null && offers.Length > 0)
        {
            Console.WriteLine($"[WSTracker] Announcing with {offers.Length} offers, infoHash={Convert.ToHexString(infoHash).ToLowerInvariant()[..16]}..., event={eventStr ?? "none"}");
            var msg = new
            {
                action = "announce",
                info_hash = TrackerEncoding.ToBinaryString(infoHash),
                peer_id = TrackerEncoding.ToBinaryString(_peerId),
                uploaded,
                downloaded,
                left,
                port,
                @event = eventStr,
                numwant = Math.Min(offers.Length, 5),
                offers = offers.Select(o => new
                {
                    offer = new { type = o.Offer.Type, sdp = o.Offer.Sdp },
                    offer_id = o.OfferId,
                }).ToArray(),
            };
            await SendJsonAsync(msg, ct);
        }
        else
        {
            var msg = new TrackerAnnounceMessage
            {
                Action = "announce",
                InfoHash = TrackerEncoding.ToBinaryString(infoHash),
                PeerId = TrackerEncoding.ToBinaryString(_peerId),
                Uploaded = uploaded,
                Downloaded = downloaded,
                Left = left,
                Port = port,
                Event = eventStr,
            };
            await SendJsonAsync(msg, ct);
        }
    }

    /// <summary>Send WebRTC offer to a peer via tracker relay.</summary>
    public async Task SendOfferAsync(string toPeerId, JsonElement offer, string offerId,
        CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open || _currentInfoHash == null) return;

        var msg = new
        {
            action = "announce",
            info_hash = TrackerEncoding.ToBinaryString(_currentInfoHash),
            peer_id = TrackerEncoding.ToBinaryString(_peerId),
            to_peer_id = toPeerId,
            offer,
            offer_id = offerId,
        };

        await SendJsonAsync(msg, ct);
    }

    /// <summary>Send WebRTC answer to a peer via tracker relay.</summary>
    public async Task SendAnswerAsync(string toPeerId, JsonElement answer, string offerId,
        CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open || _currentInfoHash == null) return;

        var msg = new
        {
            action = "announce",
            info_hash = TrackerEncoding.ToBinaryString(_currentInfoHash),
            peer_id = TrackerEncoding.ToBinaryString(_peerId),
            to_peer_id = toPeerId,
            answer,
            offer_id = offerId,
        };

        await SendJsonAsync(msg, ct);
    }

    private async Task ReannounceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_announceIntervalMs, ct);
                if (_currentInfoHash != null && _ws?.State == WebSocketState.Open)
                {
                    var offers = _offerFactory?.Invoke();
                    await AnnounceAsync(_currentInfoHash, _currentPort, 0, 0, 0, offers, TrackerEvent.None, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public async Task StopAsync()
    {
        _readCts?.Cancel();

        if (_ws?.State == WebSocketState.Open && _currentInfoHash != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await AnnounceAsync(_currentInfoHash, _currentPort, 0, 0, 0, null, TrackerEvent.Stopped, cts.Token);
            }
            catch { }
        }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
            }
            catch { }
        }
        if (_readLoop != null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        OnDisconnected?.Invoke();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var msgBuffer = new List<byte>();

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                msgBuffer.AddRange(buffer.AsSpan(0, result.Count).ToArray());

                if (result.EndOfMessage)
                {
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(msgBuffer.ToArray());
                        ProcessMessage(json);
                    }
                    msgBuffer.Clear();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            OnDisconnected?.Invoke();
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("action", out var actionProp)) return;
            var action = actionProp.GetString();

            if (root.TryGetProperty("offer", out _) && root.TryGetProperty("offer_id", out _))
            {
                ProcessOffer(root);
            }
            else if (root.TryGetProperty("answer", out _) && root.TryGetProperty("offer_id", out _))
            {
                ProcessAnswer(root);
            }
            else
            {
                switch (action)
                {
                    case "announce":
                        ProcessAnnounce(root);
                        break;
                    case "offer":
                        ProcessOffer(root);
                        break;
                    case "answer":
                        ProcessAnswer(root);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Message parse error: {ex.Message}");
        }
    }

    private void ProcessAnnounce(JsonElement root)
    {
        if (root.TryGetProperty("failure reason", out var failProp))
        {
            var reason = failProp.GetString();
            Console.WriteLine($"[WSTracker] Announce FAILURE: {reason}");
            OnError?.Invoke($"Tracker failure: {reason}");
            return;
        }

        if (root.TryGetProperty("interval", out var interval))
        {
            var secs = interval.GetInt32();
            secs = Math.Min(secs, 3600);
            _announceIntervalMs = secs * 1000;
        }

        int seeders = root.TryGetProperty("complete", out var c) ? c.GetInt32() : 0;
        int leechers = root.TryGetProperty("incomplete", out var ic) ? ic.GetInt32() : 0;
        Console.WriteLine($"[WSTracker] Announce response: seeders={seeders}, leechers={leechers}");
        OnAnnounceResponse?.Invoke(seeders, leechers);

        if (root.TryGetProperty("peers", out var peers) && peers.ValueKind == JsonValueKind.Array)
        {
            foreach (var peer in peers.EnumerateArray())
            {
                if (peer.TryGetProperty("peer_id", out var pidProp))
                {
                    var peerId = pidProp.GetString();
                    if (peerId != null)
                    {
                        OnPeer?.Invoke(new PeerInfo
                        {
                            Address = peerId,
                            Source = "ws-tracker",
                        });
                    }
                }
            }
        }
    }

    private void ProcessOffer(JsonElement root)
    {
        var fromPeerId = root.TryGetProperty("peer_id", out var pid) ? pid.GetString() : null;
        var offerId = root.TryGetProperty("offer_id", out var oid) ? oid.GetString() : null;
        Console.WriteLine($"[WSTracker] OFFER received from={fromPeerId?[..Math.Min(16, fromPeerId?.Length ?? 0)]} offerId={offerId?[..Math.Min(8, offerId?.Length ?? 0)]}");
        if (fromPeerId == null || offerId == null) return;

        if (root.TryGetProperty("offer", out var offer))
            OnOffer?.Invoke(fromPeerId, offerId, offer.Clone());
    }

    private void ProcessAnswer(JsonElement root)
    {
        var fromPeerId = root.TryGetProperty("peer_id", out var pid) ? pid.GetString() : null;
        var offerId = root.TryGetProperty("offer_id", out var oid) ? oid.GetString() : null;
        Console.WriteLine($"[WSTracker] ANSWER received from={fromPeerId?[..Math.Min(16, fromPeerId?.Length ?? 0)]} offerId={offerId?[..Math.Min(8, offerId?.Length ?? 0)]}");
        if (fromPeerId == null || offerId == null) return;

        if (root.TryGetProperty("answer", out var answer))
            OnAnswer?.Invoke(fromPeerId, offerId, answer.Clone());
    }

    private async Task SendJsonAsync<T>(T obj, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ws?.Dispose();
    }
}

/// <summary>Tracker announce message format.</summary>
internal class TrackerAnnounceMessage
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("info_hash")]
    public string InfoHash { get; set; } = "";

    [JsonPropertyName("peer_id")]
    public string PeerId { get; set; } = "";

    [JsonPropertyName("uploaded")]
    public long Uploaded { get; set; }

    [JsonPropertyName("downloaded")]
    public long Downloaded { get; set; }

    [JsonPropertyName("left")]
    public long Left { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("numwant")]
    public int? Numwant { get; set; }
}
