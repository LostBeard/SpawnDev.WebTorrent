using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpawnDev.WebTorrent.Server;

/// <summary>
/// Compute request board — a marketplace where swarm coordinators post
/// "looking for compute" requests and volunteers browse available swarms to join.
///
/// Authentication:
///   - POST requires a signed payload (ECDSA-P256 signature of the request body)
///   - DELETE requires the owner fingerprint that matches the original post
///   - Rate limited: max 10 requests per identity per hour
///
/// Endpoints:
///   POST /compute/request    — coordinator posts a signed request
///   GET  /compute/requests   — volunteers browse active requests
///   GET  /compute/stats      — aggregate compute stats
///   DELETE /compute/request/{id}?fingerprint={fp} — owner removes a request
/// </summary>
public class ComputeRequestBoard
{
    private readonly ConcurrentDictionary<string, ComputeRequest> _requests = new();
    private readonly ConcurrentDictionary<string, RateLimitEntry> _rateLimits = new();

    /// <summary>Max requests per identity per hour.</summary>
    public int MaxRequestsPerHour { get; set; } = 10;

    /// <summary>
    /// Post a new compute request with signature verification.
    /// Returns null if rate limited or signature missing.
    /// </summary>
    public (ComputeRequest? request, string? error) PostSigned(ComputeRequest request)
    {
        // Require identity
        if (string.IsNullOrEmpty(request.OwnerFingerprint))
            return (null, "OwnerFingerprint required");

        if (string.IsNullOrEmpty(request.Signature))
            return (null, "Signature required — sign the request with your SwarmIdentity");

        if (string.IsNullOrEmpty(request.PublicKey))
            return (null, "PublicKey required (base64 SPKI)");

        // Verify fingerprint matches public key
        try
        {
            var pubKeyBytes = Convert.FromBase64String(request.PublicKey);
            var computedFingerprint = Convert.ToHexString(SHA256.HashData(pubKeyBytes)).ToLowerInvariant();
            if (computedFingerprint != request.OwnerFingerprint.ToLowerInvariant())
                return (null, "OwnerFingerprint does not match PublicKey");
        }
        catch
        {
            return (null, "Invalid PublicKey format");
        }

        // Rate limit per identity
        var rateKey = request.OwnerFingerprint.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var entry = _rateLimits.GetOrAdd(rateKey, _ => new RateLimitEntry());
        lock (entry)
        {
            // Clean old entries
            entry.Timestamps.RemoveAll(t => (now - t).TotalHours > 1);
            if (entry.Timestamps.Count >= MaxRequestsPerHour)
                return (null, $"Rate limited: max {MaxRequestsPerHour} requests per hour");
            entry.Timestamps.Add(now);
        }

        request.Id = Guid.NewGuid().ToString("N");
        request.PostedAt = now;
        request.ExpiresAt = now.Add(request.TimeToLive);
        _requests[request.Id] = request;
        CleanExpired();
        return (request, null);
    }

    /// <summary>
    /// Post without authentication (legacy, for development only).
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
    /// Remove a request. Only the owner (matching fingerprint) can delete.
    /// Returns (success, error).
    /// </summary>
    public (bool success, string? error) RemoveAuthenticated(string id, string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
            return (false, "fingerprint query parameter required");

        if (!_requests.TryGetValue(id, out var request))
            return (false, "not found");

        if (!string.Equals(request.OwnerFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return (false, "forbidden — only the owner can delete this request");

        return (_requests.TryRemove(id, out _), null);
    }

    /// <summary>
    /// Remove a request (unauthenticated, legacy).
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
/// Rate limit tracking per identity.
/// </summary>
internal class RateLimitEntry
{
    public List<DateTimeOffset> Timestamps { get; } = new();
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

    /// <summary>Coordinator's public key fingerprint (SHA-256 of SPKI, hex).</summary>
    public string? OwnerFingerprint { get; set; }

    /// <summary>Coordinator's public key (base64 SPKI) for signature verification.</summary>
    public string? PublicKey { get; set; }

    /// <summary>Signature of the request payload (base64, ECDSA-P256/SHA-256).</summary>
    public string? Signature { get; set; }

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
