using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SpawnDev.WebTorrent.Discovery;

/// <summary>
/// WebSocket tracker client. Connects to a WebTorrent tracker server,
/// announces torrents, receives peer lists, and relays WebRTC signaling.
///
/// Compatible with any BEP 15 WebSocket tracker (webtorrent-tracker protocol).
/// Works in both desktop (.NET WebSocket) and browser (Blazor WASM WebSocket).
/// </summary>
public class TrackerClient : IDiscovery
{
    private readonly string _trackerUrl;
    private readonly string _peerId;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;

    public string Type => "tracker";

    public event Action<PeerInfo>? OnPeer;
    public event Action<TrackerAnnounceResponse>? OnAnnounce;
    public event Action<string>? OnError;

    public TrackerClient(string trackerUrl, string peerId)
    {
        _trackerUrl = trackerUrl;
        _peerId = peerId;
    }

    public async Task StartAsync(byte[] infoHash, int port, CancellationToken ct = default)
    {
        _ws = new ClientWebSocket();
        _readCts = new CancellationTokenSource();

        try
        {
            await _ws.ConnectAsync(new Uri(_trackerUrl), ct);
            _readLoop = ReadLoopAsync(_readCts.Token);

            // Send initial announce
            await AnnounceAsync(infoHash, port, 0, 0, 0, ct);
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

        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = Convert.ToHexString(infoHash).ToLowerInvariant(),
            peer_id = _peerId,
            uploaded,
            downloaded,
            left,
            port,
        });

        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Send a WebRTC offer to a specific peer via the tracker (signaling relay).</summary>
    public async Task SendOfferAsync(byte[] infoHash, string toPeerId, object offer, string offerId,
        CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = JsonSerializer.Serialize(new
        {
            action = "offer",
            info_hash = Convert.ToHexString(infoHash).ToLowerInvariant(),
            peer_id = _peerId,
            to_peer_id = toPeerId,
            offer,
            offer_id = offerId,
        });

        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Send a WebRTC answer to a specific peer via the tracker.</summary>
    public async Task SendAnswerAsync(byte[] infoHash, string toPeerId, object answer, string offerId,
        CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = JsonSerializer.Serialize(new
        {
            action = "answer",
            info_hash = Convert.ToHexString(infoHash).ToLowerInvariant(),
            peer_id = _peerId,
            to_peer_id = toPeerId,
            answer,
            offer_id = offerId,
        });

        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    public async Task StopAsync()
    {
        _readCts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch { }
        }
        if (_readLoop != null)
        {
            try { await _readLoop; } catch { }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                ProcessMessage(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();

            switch (action)
            {
                case "announce":
                    if (root.TryGetProperty("peers", out var peers))
                    {
                        foreach (var peer in peers.EnumerateArray())
                        {
                            var peerId = peer.GetProperty("peer_id").GetString();
                            if (peerId != null && peerId != _peerId)
                            {
                                OnPeer?.Invoke(new PeerInfo
                                {
                                    Address = peerId, // WebRTC peers use peer ID, not IP
                                    Source = "tracker",
                                });
                            }
                        }
                    }

                    int complete = root.TryGetProperty("complete", out var c) ? c.GetInt32() : 0;
                    int incomplete = root.TryGetProperty("incomplete", out var ic) ? ic.GetInt32() : 0;
                    OnAnnounce?.Invoke(new TrackerAnnounceResponse
                    {
                        Seeders = complete,
                        Leechers = incomplete,
                    });
                    break;

                case "offer":
                    // WebRTC signaling — relay offer to peer connection logic
                    break;

                case "answer":
                    // WebRTC signaling — relay answer to peer connection logic
                    break;
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Tracker message parse error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ws?.Dispose();
    }
}

/// <summary>Response from a tracker announce.</summary>
public record TrackerAnnounceResponse
{
    public int Seeders { get; init; }
    public int Leechers { get; init; }
}
