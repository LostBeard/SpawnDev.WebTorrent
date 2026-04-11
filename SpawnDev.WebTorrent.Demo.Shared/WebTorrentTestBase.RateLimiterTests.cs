using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task RateLimiter_Unlimited_PassesThrough()
    {
        var limiter = new RateLimiter(-1);
        var task = limiter.WaitAsync(1024);
        if (!task.IsCompleted) throw new Exception("Unlimited should return immediately");
    }

    [TestMethod]
    public async Task RateLimiter_Zero_Pauses()
    {
        var limiter = new RateLimiter(0); // paused
        using var cts = new CancellationTokenSource(500);
        bool completed = false;
        try
        {
            await limiter.WaitAsync(1, cts.Token);
            completed = true;
        }
        catch (OperationCanceledException) { }

        if (completed) throw new Exception("Rate=0 should block indefinitely (timeout expected)");
    }

    [TestMethod]
    public async Task RateLimiter_Throttle_CorrectRate()
    {
        var limiter = new RateLimiter(8192); // 8KB/sec
        await limiter.WaitAsync(8192); // consume initial tokens
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(4096); // need ~0.5s of refill
        sw.Stop();
        if (sw.ElapsedMilliseconds < 300)
            throw new Exception($"Expected delay for refill, got {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public async Task RateLimiter_ClientThrottle_AffectsRate()
    {
        var client = CreateIsolatedClient();
        // Default should be unlimited
        if (client.UploadRateLimiter.Rate != -1)
            throw new Exception($"Default UL rate should be -1, got {client.UploadRateLimiter.Rate}");
        if (client.DownloadRateLimiter.Rate != -1)
            throw new Exception($"Default DL rate should be -1, got {client.DownloadRateLimiter.Rate}");

        // Throttle and verify the rate changes
        client.ThrottleDownload(50000);
        client.ThrottleUpload(25000);
        if (client.DownloadRateLimiter.Rate != 50000)
            throw new Exception($"DL rate should be 50000 after throttle, got {client.DownloadRateLimiter.Rate}");
        if (client.UploadRateLimiter.Rate != 25000)
            throw new Exception($"UL rate should be 25000 after throttle, got {client.UploadRateLimiter.Rate}");

        // Reset to unlimited
        client.ThrottleDownload(-1);
        client.ThrottleUpload(-1);
        if (client.DownloadRateLimiter.Rate != -1)
            throw new Exception("DL rate should be -1 after reset");
        if (client.UploadRateLimiter.Rate != -1)
            throw new Exception("UL rate should be -1 after reset");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task RateLimiter_Paused_BlocksThenResumes()
    {
        // Verify that rate=0 blocks and changing to unlimited releases
        var limiter = new RateLimiter(0); // paused
        using var cts = new CancellationTokenSource(300);
        bool blocked = false;
        try { await limiter.WaitAsync(1, cts.Token); }
        catch (OperationCanceledException) { blocked = true; }
        if (!blocked) throw new Exception("Rate=0 should block");

        // Now set to unlimited and verify it passes
        limiter.Rate = -1;
        var task = limiter.WaitAsync(1024);
        if (!task.IsCompleted) throw new Exception("Rate=-1 should pass immediately after unpausing");
    }
}
