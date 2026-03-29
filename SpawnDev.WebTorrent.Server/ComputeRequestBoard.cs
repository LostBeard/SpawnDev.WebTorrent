using System.Collections.Concurrent;

namespace SpawnDev.WebTorrent.Server;

/// <summary>
/// Compute request board — a marketplace where swarm coordinators post
/// "looking for compute" requests and volunteers browse available swarms to join.
///
/// This is the meeting point between compute demand and supply.
/// Sovereign AI swarms can post requests here too.
///
/// Endpoints:
///   POST /compute/request    — coordinator posts a request
///   GET  /compute/requests   — volunteers browse active requests
///   GET  /compute/stats      — aggregate compute stats
///   DELETE /compute/request/{id} — coordinator removes a request
/// </summary>
public class ComputeRequestBoard
{
    private readonly ConcurrentDictionary<string, ComputeRequest> _requests = new();

    /// <summary>
    /// Post a new compute request.
    /// </summary>
    public ComputeRequest Post(ComputeRequest request)
    {
        request.Id = Guid.NewGuid().ToString("N");
        request.PostedAt = DateTimeOffset.UtcNow;
        request.ExpiresAt = DateTimeOffset.UtcNow.Add(request.TimeToLive);
        _requests[request.Id] = request;
        CleanExpired();
        return request;
    }

    /// <summary>
    /// Get all active (non-expired) requests.
    /// </summary>
    public IReadOnlyList<ComputeRequest> GetActive()
    {
        CleanExpired();
        return _requests.Values
            .Where(r => r.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(r => r.PostedAt)
            .ToList();
    }

    /// <summary>
    /// Get aggregate stats.
    /// </summary>
    public ComputeStats GetStats()
    {
        var active = GetActive();
        return new ComputeStats
        {
            ActiveRequests = active.Count,
            TotalTflopsNeeded = active.Sum(r => r.TflopsNeeded),
            TotalTflopsAvailable = active.Sum(r => r.TflopsAvailable),
            UniqueSwarms = active.Select(r => r.SwarmName).Distinct().Count(),
        };
    }

    /// <summary>
    /// Remove a request.
    /// </summary>
    public bool Remove(string id)
    {
        return _requests.TryRemove(id, out _);
    }

    /// <summary>
    /// Update TFLOPS available (called when peers join the swarm).
    /// </summary>
    public void UpdateAvailable(string id, double tflopsAvailable, int peerCount)
    {
        if (_requests.TryGetValue(id, out var request))
        {
            request.TflopsAvailable = tflopsAvailable;
            request.PeerCount = peerCount;
        }
    }

    private void CleanExpired()
    {
        var expired = _requests.Where(kv => kv.Value.ExpiresAt <= DateTimeOffset.UtcNow)
            .Select(kv => kv.Key).ToList();
        foreach (var key in expired)
            _requests.TryRemove(key, out _);
    }
}

/// <summary>
/// A compute request posted to the board.
/// </summary>
public class ComputeRequest
{
    /// <summary>Unique request ID (server-assigned).</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable swarm name.</summary>
    public string SwarmName { get; set; } = "";

    /// <summary>What the swarm is computing (e.g., "Phi-4 Inference", "Protein Folding").</summary>
    public string Purpose { get; set; } = "";

    /// <summary>Coordinator's public key fingerprint.</summary>
    public string? OwnerFingerprint { get; set; }

    /// <summary>TFLOPS needed for the workload.</summary>
    public double TflopsNeeded { get; set; }

    /// <summary>TFLOPS currently available in the swarm.</summary>
    public double TflopsAvailable { get; set; }

    /// <summary>Current peer count.</summary>
    public int PeerCount { get; set; }

    /// <summary>Magnet link for joining.</summary>
    public string? MagnetLink { get; set; }

    /// <summary>HTTP join link (QR-friendly).</summary>
    public string? JoinLink { get; set; }

    /// <summary>When the request was posted.</summary>
    public DateTimeOffset PostedAt { get; set; }

    /// <summary>When the request expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>How long the request stays active. Default: 1 hour.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Aggregate compute stats for the board.
/// </summary>
public class ComputeStats
{
    public int ActiveRequests { get; set; }
    public double TotalTflopsNeeded { get; set; }
    public double TotalTflopsAvailable { get; set; }
    public int UniqueSwarms { get; set; }
}
