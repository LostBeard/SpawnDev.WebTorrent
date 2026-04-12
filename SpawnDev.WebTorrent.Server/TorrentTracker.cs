using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SpawnDev.WebTorrent.Server;

/// <summary>
/// WebSocket-based BitTorrent tracker (BEP 15 over WebSocket).
/// Manages peer swarms and facilitates WebRTC signaling for browser peers.
///
/// Usage:
///   app.Map("/announce", tracker.HandleWebSocket);
/// </summary>
public class TorrentTracker
{
    private readonly ConcurrentDictionary<string, TorrentSwarmInfo> _swarms = new();
    private readonly TrackerOptions _options;

    // JSON options matching JS behavior: no \uXXXX escaping for binary strings (info_hash, peer_id, offer_id)
    private static readonly JsonSerializerOptions _jsonWriteOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialize to JSON matching JS wire format - no \u00XX escapes for C1 control chars.</summary>
    private static string SerializeJson(object value)
    {
        var json = JsonSerializer.Serialize(value, _jsonWriteOpts);
        // System.Text.Json escapes C1 control chars (0x80-0x9F) even with UnsafeRelaxedJsonEscaping.
        // JS JSON.stringify does NOT escape them. Replace to match JS wire format.
        // Only un-escape 0x80-0xFF (C1 + latin1 upper). Leave 0x00-0x1F escaped (valid JSON).
        return System.Text.RegularExpressions.Regex.Replace(json, @"\\u00([89a-fA-F][0-9a-fA-F])", m =>
            ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString());
    }
    private static readonly JsonSerializerOptions _jsonReadOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public TorrentTracker(TrackerOptions? options = null)
    {
        _options = options ?? new TrackerOptions();
    }

    /// <summary>Active swarms being tracked.</summary>
    public IReadOnlyDictionary<string, TorrentSwarmInfo> Swarms => _swarms;

    /// <summary>Total peers across all swarms.</summary>
    public int TotalPeers => _swarms.Values.Sum(s => s.Peers.Count);

    /// <summary>Handle an incoming WebSocket connection from a peer.</summary>
    public async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        var peer = new TrackerPeer
        {
            WebSocket = ws,
            ConnectedAt = DateTimeOffset.UtcNow,
            RemoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        };

        try
        {
            await HandlePeerMessages(peer);
        }
        finally
        {
            // Remove peer from all swarms on disconnect
            foreach (var swarm in _swarms.Values)
                swarm.Peers.TryRemove(peer.PeerId, out _);
        }
    }

    private async Task HandlePeerMessages(TrackerPeer peer)
    {
        var buffer = new byte[16384];

        while (peer.WebSocket.State == WebSocketState.Open)
        {
            var received = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await peer.WebSocket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
                received.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            if (received.Length > 1_000_000)
            {
                received.SetLength(0);
                continue;
            }

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
                var msg = JsonSerializer.Deserialize<TrackerMessage>(json, _jsonReadOpts);
                if (msg == null) continue;

                switch (msg.Action)
                {
                    case "announce":
                        await HandleAnnounce(peer, msg);
                        break;
                    case "offer":
                        await HandleOffer(peer, msg);
                        break;
                    case "answer":
                        await HandleAnswer(peer, msg);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tracker] Error: {ex.Message}");
            }
        }
    }

    private async Task HandleAnnounce(TrackerPeer peer, TrackerMessage msg)
    {
        if (string.IsNullOrEmpty(msg.InfoHash) || string.IsNullOrEmpty(msg.PeerId))
        {
            await SendText(peer, "{\"action\":\"announce\",\"failure reason\":\"missing info_hash or peer_id\"}");
            return;
        }

        if (msg.InfoHash.Length > 100) return;

        peer.PeerId = msg.PeerId;
        var swarm = _swarms.GetOrAdd(msg.InfoHash, _ => new TorrentSwarmInfo { InfoHash = msg.InfoHash });
        swarm.Peers[peer.PeerId] = peer;

        // Handle events
        if (msg.Event == "stopped")
        {
            swarm.Peers.TryRemove(peer.PeerId, out _);
            return;
        }
        if (msg.Event == "completed" || msg.Left == 0)
            peer.IsSeeder = true;

        var maxPeers = Math.Min(msg.Numwant ?? _options.MaxPeersPerAnnounce, _options.MaxPeersPerAnnounce);

        var otherPeers = swarm.Peers.Values
            .Where(p => p.PeerId != peer.PeerId)
            .Take(maxPeers)
            .Select(p => new { peer_id = p.PeerId })
            .ToArray();

        var response = SerializeJson(new
        {
            action = "announce",
            info_hash = msg.InfoHash,
            interval = _options.AnnounceInterval,
            complete = swarm.Peers.Count(p => p.Value.IsSeeder),
            incomplete = swarm.Peers.Count(p => !p.Value.IsSeeder),
            peers = otherPeers,
        });

        await SendText(peer, response);

        // Relay pre-generated offers to existing peers in the swarm.
        // WebTorrent protocol: client sends offers WITH announce, server
        // distributes them to other peers so they can create answers.
        if (msg.Offers is JsonElement offersElement && offersElement.ValueKind == JsonValueKind.Array)
        {
            var existingPeers = swarm.Peers.Values
                .Where(p => p.PeerId != peer.PeerId && p.WebSocket.State == WebSocketState.Open)
                .OrderBy(_ => Random.Shared.Next())
                .ToArray();

            int offerIdx = 0;
            foreach (var offer in offersElement.EnumerateArray())
            {
                if (offerIdx >= existingPeers.Length) break;
                var target = existingPeers[offerIdx];

                if (offer.TryGetProperty("offer", out var offerSdp) &&
                    offer.TryGetProperty("offer_id", out var offerId))
                {
                    var forward = SerializeJson(new
                    {
                        action = "announce",
                        info_hash = msg.InfoHash,
                        peer_id = peer.PeerId,
                        offer = offerSdp,
                        offer_id = offerId,
                    });
                    await SendText(target, forward);
                }

                offerIdx++;
            }
        }

        // Handle answer embedded in announce (WebTorrent unified protocol)
        bool hasAnswer = msg.Answer is JsonElement ae && ae.ValueKind == JsonValueKind.Object;
        if (hasAnswer && !string.IsNullOrEmpty(msg.ToPeerId) && !string.IsNullOrEmpty(msg.OfferId))
        {
            var answerElement = (JsonElement)msg.Answer!;
            bool foundTarget = swarm.Peers.TryGetValue(msg.ToPeerId, out var target);
            Console.WriteLine($"[Tracker] Answer relay: to={msg.ToPeerId[..Math.Min(12, msg.ToPeerId.Length)]}, found={foundTarget}, wsOpen={target?.WebSocket.State}");
            if (foundTarget && target!.WebSocket.State == WebSocketState.Open)
            {
                var forward = SerializeJson(new
                {
                    action = "announce",
                    info_hash = msg.InfoHash,
                    peer_id = peer.PeerId,
                    answer = answerElement,
                    offer_id = msg.OfferId,
                });
                Console.WriteLine($"[Tracker] Forwarding answer ({forward.Length} bytes)");
                await SendText(target, forward);
            }
        }
        else if (!hasAnswer)
        {
            Console.WriteLine($"[Tracker] No answer in announce from {peer.PeerId?[..Math.Min(12, peer.PeerId?.Length ?? 0)]}");
        }
    }

    /// <summary>Forward WebRTC offer from one peer to another (signaling relay).</summary>
    private async Task HandleOffer(TrackerPeer peer, TrackerMessage msg)
    {
        if (string.IsNullOrEmpty(msg.InfoHash) || string.IsNullOrEmpty(msg.ToPeerId)) return;
        if (!_swarms.TryGetValue(msg.InfoHash, out var swarm)) return;
        if (!swarm.Peers.TryGetValue(msg.ToPeerId, out var target)) return;

        var forward = SerializeJson(new
        {
            action = "offer",
            info_hash = msg.InfoHash,
            peer_id = peer.PeerId,
            offer = msg.Offer,
            offer_id = msg.OfferId,
        });

        await SendText(target, forward);
    }

    /// <summary>Forward WebRTC answer from one peer to another.</summary>
    private async Task HandleAnswer(TrackerPeer peer, TrackerMessage msg)
    {
        if (string.IsNullOrEmpty(msg.InfoHash) || string.IsNullOrEmpty(msg.ToPeerId)) return;
        if (!_swarms.TryGetValue(msg.InfoHash, out var swarm)) return;
        if (!swarm.Peers.TryGetValue(msg.ToPeerId, out var target)) return;

        var forward = SerializeJson(new
        {
            action = "answer",
            info_hash = msg.InfoHash,
            peer_id = peer.PeerId,
            answer = msg.Answer,
            offer_id = msg.OfferId,
        });

        await SendText(target, forward);
    }

    private static async Task SendText(TrackerPeer peer, string text)
    {
        if (peer.WebSocket.State != WebSocketState.Open) return;
        if (!await peer.SendLock.WaitAsync(5000)) return;
        try
        {
            if (peer.WebSocket.State != WebSocketState.Open) return;
            using var cts = new CancellationTokenSource(10_000);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await peer.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { peer.SendLock.Release(); }
    }
}

/// <summary>Tracker configuration.</summary>
public class TrackerOptions
{
    /// <summary>Announce interval in seconds (how often peers re-announce).</summary>
    public int AnnounceInterval { get; set; } = 120;

    /// <summary>Max peers returned per announce response.</summary>
    public int MaxPeersPerAnnounce { get; set; } = 50;
}

/// <summary>A connected peer on the tracker.</summary>
public class TrackerPeer
{
    public WebSocket WebSocket { get; set; } = null!;
    public string PeerId { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public DateTimeOffset ConnectedAt { get; set; }
    public bool IsSeeder { get; set; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
}

/// <summary>A torrent swarm tracked by this server.</summary>
public class TorrentSwarmInfo
{
    public string InfoHash { get; set; } = "";
    public ConcurrentDictionary<string, TrackerPeer> Peers { get; } = new();
}

/// <summary>WebSocket tracker message format.</summary>
public class TrackerMessage
{
    public string? Action { get; set; }
    public string? InfoHash { get; set; }
    public string? PeerId { get; set; }
    public string? ToPeerId { get; set; }
    public string? OfferId { get; set; }
    public string? Event { get; set; }
    public JsonElement? Offer { get; set; }
    public JsonElement? Answer { get; set; }
    public JsonElement? Offers { get; set; }
    public int? Numwant { get; set; }
    public long? Downloaded { get; set; }
    public long? Uploaded { get; set; }
    public long? Left { get; set; }
}

/// <summary>
/// Web seed server — serves torrent pieces over HTTP range requests.
/// Clients request byte ranges; the server reads from local storage and responds.
/// </summary>
public class WebSeedServer
{
    private readonly string _storageRoot;

    public WebSeedServer(string storageRoot)
    {
        _storageRoot = storageRoot;
        Directory.CreateDirectory(_storageRoot);
    }

    /// <summary>Handle an HTTP request for a file in a torrent.</summary>
    public async Task HandleRequest(HttpContext context, string infoHash, string filePath)
    {
        var localPath = Path.GetFullPath(Path.Combine(_storageRoot, infoHash, filePath));
        // SECURITY: Prevent path traversal attacks (e.g., ../../etc/passwd)
        if (!localPath.StartsWith(Path.GetFullPath(_storageRoot), StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (!System.IO.File.Exists(localPath))
        {
            context.Response.StatusCode = 404;
            return;
        }

        var fileInfo = new FileInfo(localPath);
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["Accept-Ranges"] = "bytes";

        // Handle Range header for partial content (BEP 17/19)
        if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            var range = rangeHeader.ToString();
            if (range.StartsWith("bytes="))
            {
                var parts = range.Substring(6).Split('-');
                long start = long.Parse(parts[0]);
                long end = parts.Length > 1 && !string.IsNullOrEmpty(parts[1])
                    ? long.Parse(parts[1])
                    : fileInfo.Length - 1;

                int length = (int)(end - start + 1);
                context.Response.StatusCode = 206;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{fileInfo.Length}";
                context.Response.ContentLength = length;

                using var fs = System.IO.File.OpenRead(localPath);
                fs.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[Math.Min(length, 65536)];
                int remaining = length;
                while (remaining > 0)
                {
                    int toRead = Math.Min(remaining, buffer.Length);
                    int read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
                    if (read == 0) break;
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read));
                    remaining -= read;
                }
                return;
            }
        }

        // Full file
        context.Response.ContentLength = fileInfo.Length;
        await context.Response.SendFileAsync(localPath);
    }
}

/// <summary>
/// Extension methods for registering the WebTorrent server in ASP.NET.
/// </summary>
public static class WebTorrentServerExtensions
{
    /// <summary>
    /// Add WebTorrent tracker and web seed endpoints to the application.
    /// </summary>
    public static void MapWebTorrentServer(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app,
        TorrentTracker tracker, WebSeedServer? webSeed = null)
    {
        app.Map("/announce", tracker.HandleWebSocket);

        if (webSeed != null)
        {
            app.MapGet("/seed/{infoHash}/{**filePath}", async (HttpContext ctx, string infoHash, string filePath) =>
            {
                await webSeed.HandleRequest(ctx, infoHash, filePath);
            });
        }

        app.MapGet("/stats", () => new
        {
            swarms = tracker.Swarms.Count,
            totalPeers = tracker.TotalPeers,
            swarmDetails = tracker.Swarms.Select(s => new
            {
                infoHash = s.Key,
                peers = s.Value.Peers.Count,
            })
        });
    }
}
