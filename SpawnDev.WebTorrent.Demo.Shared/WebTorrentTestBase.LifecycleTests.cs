using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Phase 1 lifecycle bug-fix tests: Pause / Resume, Remove / RemoveWithData,
/// RateLimiter unlimited vs throttled, ServiceWorkerStreamHandler registration.
/// Migrated from NUnit LifecycleTests.cs.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Lifecycle_Pause_StopsBlockRequests()
    {
        var client = new WebTorrentClient();
        var data = LifecycleTests_MakeData(32768, seed: 401);
        var torrent = await client.SeedAsync("pause-test.bin", data);

        if (!torrent.Done) throw new Exception("seeded torrent should be Done");
        if (torrent.Paused) throw new Exception("should not start paused");

        torrent.Pause();
        if (!torrent.Paused) throw new Exception("should be Paused after Pause()");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Lifecycle_Resume_RestartsAfterPause()
    {
        var client = new WebTorrentClient();
        var data = LifecycleTests_MakeData(32768, seed: 402);
        var torrent = await client.SeedAsync("resume-test.bin", data);

        torrent.Pause();
        if (!torrent.Paused) throw new Exception("pause did not set Paused");

        torrent.Resume();
        if (torrent.Paused) throw new Exception("resume did not clear Paused");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Lifecycle_Remove_DisposesTorrent()
    {
        var client = new WebTorrentClient();
        var data = LifecycleTests_MakeData(16384, seed: 403);
        var torrent = await client.SeedAsync("remove-test.bin", data);

        if (torrent.Destroyed) throw new Exception("should not be Destroyed before Remove");

        await client.RemoveAsync(torrent);

        if (!torrent.Destroyed) throw new Exception("should be Destroyed after RemoveAsync");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Lifecycle_Remove_NotInClientList()
    {
        var client = new WebTorrentClient();
        var data = LifecycleTests_MakeData(16384, seed: 404);
        var torrent = await client.SeedAsync("remove-list-test.bin", data);
        var infoHash = torrent.InfoHash;

        if (client.Get(infoHash!) is null) throw new Exception("torrent should be present before Remove");

        await client.RemoveAsync(torrent);

        if (client.Get(infoHash!) is not null) throw new Exception("torrent should be gone after Remove");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Lifecycle_RemoveWithData_ClearsMemoryStore()
    {
        var client = new WebTorrentClient();
        var data = LifecycleTests_MakeData(16384, seed: 405);
        var torrent = await client.SeedAsync("rmdata-test.bin", data);
        var infoHash = torrent.InfoHash;

        if (!torrent.Done) throw new Exception("seeded torrent should be Done");
        if (client.Torrents.Count != 1) throw new Exception($"Torrents.Count={client.Torrents.Count}, expected 1");

        await client.RemoveWithDataAsync(torrent);

        if (!torrent.Destroyed) throw new Exception("torrent should be Destroyed after RemoveWithData");
        if (client.Get(infoHash!) is not null) throw new Exception("torrent should not be in list after Remove");
        if (client.Torrents.Count != 0) throw new Exception($"Torrents.Count={client.Torrents.Count}, expected 0");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Lifecycle_RateLimiter_Unlimited_PassesThrough()
    {
        var limiter = new RateLimiter(-1);
        var task = limiter.WaitAsync(1024);
        if (!task.IsCompleted) throw new Exception("unlimited rate limiter should return immediately");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Lifecycle_RateLimiter_Throttle_MeasurableDelay()
    {
        var limiter = new RateLimiter(8192); // 8 KiB/s
        // First call consumes the initial tokens.
        await limiter.WaitAsync(8192);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(4096);
        sw.Stop();

        if (sw.ElapsedMilliseconds <= 300)
            throw new Exception($"expected a delay >300ms for token refill, got {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public async Task Lifecycle_ServiceWorkerStreamHandler_RegistersWithClient()
    {
        var handler = new ServiceWorkerStreamHandler();
        var client = new WebTorrentClient();

        if (client.StreamHandler is not null)
            throw new Exception("client.StreamHandler should start null");

        client.RegisterStreamHandler(handler);

        if (!ReferenceEquals(client.StreamHandler, handler))
            throw new Exception("RegisterStreamHandler should set the client's StreamHandler");

        handler.Dispose();
        await client.DisposeAsync();
    }

    // ---- helpers ----

    private static byte[] LifecycleTests_MakeData(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
}
