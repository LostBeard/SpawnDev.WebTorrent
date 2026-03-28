namespace SpawnDev.WebTorrent;

/// <summary>
/// Token bucket rate limiter for upload/download throttling.
/// Thread-safe. Returns immediately if tokens available, otherwise delays.
/// Set Rate to -1 for unlimited, 0 to pause all transfers.
/// </summary>
public class RateLimiter
{
    private long _tokens;
    private long _rate; // bytes per second (-1 = unlimited, 0 = paused)
    private DateTime _lastRefill;
    private readonly object _lock = new();

    /// <summary>Rate limit in bytes per second. -1 = unlimited, 0 = paused.</summary>
    public long Rate
    {
        get => _rate;
        set
        {
            lock (_lock) { _rate = value; }
        }
    }

    public RateLimiter(long rate = -1)
    {
        _rate = rate;
        _tokens = rate > 0 ? rate : 0;
        _lastRefill = DateTime.UtcNow;
    }

    /// <summary>
    /// Wait until the specified number of bytes can be sent/received.
    /// Returns immediately if rate is unlimited (-1).
    /// </summary>
    public async Task WaitAsync(int bytes, CancellationToken ct = default)
    {
        if (_rate < 0) return; // unlimited

        while (!ct.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (_rate < 0) return; // became unlimited
                if (_rate == 0) { } // paused — keep waiting

                // Refill tokens based on elapsed time
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastRefill).TotalSeconds;
                if (elapsed > 0 && _rate > 0)
                {
                    _tokens = Math.Min(_rate, _tokens + (long)(elapsed * _rate));
                    _lastRefill = now;
                }

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
            }

            await Task.Delay(10, ct);
        }
    }
}
