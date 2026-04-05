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
    public async Task RateLimiter_ClientUpload_Exists()
    {
        var client = CreateIsolatedClient();
        if (client.UploadRateLimiter == null)
            throw new Exception("UploadRateLimiter should not be null");
        if (client.UploadRateLimiter.Rate != -1)
            throw new Exception($"Default rate should be -1 (unlimited), got {client.UploadRateLimiter.Rate}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task RateLimiter_ClientDownload_Exists()
    {
        var client = CreateIsolatedClient();
        if (client.DownloadRateLimiter == null)
            throw new Exception("DownloadRateLimiter should not be null");
        if (client.DownloadRateLimiter.Rate != -1)
            throw new Exception($"Default rate should be -1 (unlimited), got {client.DownloadRateLimiter.Rate}");
        await client.DisposeAsync();
    }
}
