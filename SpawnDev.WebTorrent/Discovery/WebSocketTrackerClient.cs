using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace SpawnDev.WebTorrent.Discovery;

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
    private int _announceIntervalMs = 120_000;

    public string Type => "ws-tracker";
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action<PeerInfo>? OnPeer;
    public event Action<int, int>? OnAnnounceResponse; // seeders, leechers
    public event Action<string, string, JsonElement>? OnOffer; // fromPeerId, offerId, offer
    public event Action<string, string, JsonElement>? OnAnswer; // fromPeerId, offerId, answer
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    public WebSocketTrackerClient(string trackerUrl, byte[] peerId)
    {
        _trackerUrl = trackerUrl;
        _peerId = peerId;
    }

    public async Task StartAsync(byte[] infoHash, int port, CancellationToken ct = default)
    {
        _currentInfoHash = infoHash;
        _ws = new ClientWebSocket();
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await _ws.ConnectAsync(new Uri(_trackerUrl), ct);
            OnConnected?.Invoke();
            _readLoop = ReadLoopAsync(_readCts.Token);
            await AnnounceAsync(infoHash, port, 0, 0, 0, ct);

            // Start periodic re-announce loop
            _ = ReannounceLoopAsync(_readCts.Token);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Tracker connect failed: {ex.Message}");
        }
    }

    public async Task AnnounceAsync(byte[] infoHash, int port,
        long uploaded, long downloaded, long left, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = new TrackerAnnounceMessage
        {
            Action = "announce",
            InfoHash = Convert.ToHexString(infoHash).ToLowerInvariant(),
            PeerId = Convert.ToHexString(_peerId).ToLowerInvariant(),
            Uploaded = uploaded,
            Downloaded = downloaded,
            Left = left,
            Port = port,
        };

        await SendJsonAsync(msg, ct);
    }

    /// <summary>Send WebRTC offer to a peer via tracker relay.</summary>
    public async Task SendOfferAsync(string toPeerId, JsonElement offer, string offerId,
        CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open || _currentInfoHash == null) return;

        var msg = new
        {
            action = "offer",
            info_hash = Convert.ToHexString(_currentInfoHash).ToLowerInvariant(),
            peer_id = Convert.ToHexString(_peerId).ToLowerInvariant(),
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
            action = "answer",
            info_hash = Convert.ToHexString(_currentInfoHash).ToLowerInvariant(),
            peer_id = Convert.ToHexString(_peerId).ToLowerInvariant(),
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
                    await AnnounceAsync(_currentInfoHash, 0, 0, 0, 0, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public async Task StopAsync()
    {
        _readCts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch { }
        }
        if (_readLoop != null)
        {
            try { await _readLoop; } catch { }
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
        catch (Exception ex)
        {
            OnError?.Invoke($"Message parse error: {ex.Message}");
        }
    }

    private void ProcessAnnounce(JsonElement root)
    {
        // Update announce interval
        if (root.TryGetProperty("interval", out var interval))
            _announceIntervalMs = interval.GetInt32() * 1000;

        int seeders = root.TryGetProperty("complete", out var c) ? c.GetInt32() : 0;
        int leechers = root.TryGetProperty("incomplete", out var ic) ? ic.GetInt32() : 0;
        int peerCount = (root.TryGetProperty("peers", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Array) ? p.GetArrayLength() : 0;
        OnAnnounceResponse?.Invoke(seeders, leechers);

        // Extract peers
        if (root.TryGetProperty("peers", out var peers))
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
        if (fromPeerId == null || offerId == null) return;

        if (root.TryGetProperty("offer", out var offer))
            OnOffer?.Invoke(fromPeerId, offerId, offer);
    }

    private void ProcessAnswer(JsonElement root)
    {
        var fromPeerId = root.TryGetProperty("peer_id", out var pid) ? pid.GetString() : null;
        var offerId = root.TryGetProperty("offer_id", out var oid) ? oid.GetString() : null;
        if (fromPeerId == null || offerId == null) return;

        if (root.TryGetProperty("answer", out var answer))
            OnAnswer?.Invoke(fromPeerId, offerId, answer);
    }

    private async Task SendJsonAsync<T>(T obj, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
}
