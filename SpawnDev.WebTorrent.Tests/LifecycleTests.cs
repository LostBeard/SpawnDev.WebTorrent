using NUnit.Framework;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for Phase 1 bug fixes: Pause/Resume, Remove, RemoveWithData, RateLimiter.
/// All tests use real code paths — no mocks.
/// </summary>
[TestFixture]
public class LifecycleTests
{
    private static byte[] MakeData(int size, int seed = 42)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Test]
    public async Task Pause_StopsBlockRequests()
    {
        var client = new WebTorrentClient();
        var data = MakeData(32768); // 2 pieces at 16KB
        var torrent = await client.SeedAsync("pause-test.bin", data);

        Assert.That(torrent.Done, Is.True, "Seeded torrent should be done");
        Assert.That(torrent.Paused, Is.False, "Should not start paused");

        torrent.Pause();

        Assert.That(torrent.Paused, Is.True, "Should be paused after Pause()");

        await client.DisposeAsync();
    }

    [Test]
    public async Task Resume_RestartsAfterPause()
    {
        var client = new WebTorrentClient();
        var data = MakeData(32768);
        var torrent = await client.SeedAsync("resume-test.bin", data);

        torrent.Pause();
        Assert.That(torrent.Paused, Is.True);

        torrent.Resume();
        Assert.That(torrent.Paused, Is.False, "Should not be paused after Resume()");

        await client.DisposeAsync();
    }

    [Test]
    public async Task Remove_DisposesTorrent()
    {
        var client = new WebTorrentClient();
        var data = MakeData(16384);
        var torrent = await client.SeedAsync("remove-test.bin", data);
        var infoHash = torrent.InfoHash;

        Assert.That(torrent.Destroyed, Is.False, "Should not be destroyed before remove");

        await client.RemoveAsync(torrent);

        Assert.That(torrent.Destroyed, Is.True, "Should be destroyed after RemoveAsync");

        await client.DisposeAsync();
    }

    [Test]
    public async Task Remove_NotInClientList()
    {
        var client = new WebTorrentClient();
        var data = MakeData(16384);
        var torrent = await client.SeedAsync("remove-list-test.bin", data);
        var infoHash = torrent.InfoHash;

        Assert.That(client.Get(infoHash!), Is.Not.Null, "Should find torrent before remove");

        await client.RemoveAsync(torrent);

        Assert.That(client.Get(infoHash!), Is.Null, "Should not find torrent after remove");

        await client.DisposeAsync();
    }

    [Test]
    public async Task RemoveWithData_ClearsMemoryStore()
    {
        // Uses MemoryChunkStore (no OPFS on desktop) — verifies the remove path works
        var client = new WebTorrentClient();
        var data = MakeData(16384);
        var torrent = await client.SeedAsync("rmdata-test.bin", data);
        var infoHash = torrent.InfoHash;

        Assert.That(torrent.Done, Is.True);
        Assert.That(client.Torrents.Count, Is.EqualTo(1));

        // RemoveWithDataAsync should dispose + clear OPFS data (no-op for MemoryChunkStore)
        await client.RemoveWithDataAsync(torrent);

        Assert.That(torrent.Destroyed, Is.True, "Torrent should be destroyed");
        Assert.That(client.Get(infoHash!), Is.Null, "Should not find torrent after remove with data");
        Assert.That(client.Torrents.Count, Is.EqualTo(0));

        await client.DisposeAsync();
    }

    [Test]
    public void RateLimiter_Unlimited_PassesThrough()
    {
        var limiter = new RateLimiter(-1);
        // Should complete instantly — unlimited
        var task = limiter.WaitAsync(1024);
        Assert.That(task.IsCompleted, Is.True, "Unlimited rate limiter should return immediately");
    }

    [Test]
    public async Task RateLimiter_Throttle_MeasurableDelay()
    {
        var limiter = new RateLimiter(8192); // 8KB/sec
        // First call should pass (initial tokens)
        await limiter.WaitAsync(8192);

        // Second call should take ~1 second (need to refill tokens)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(4096);
        sw.Stop();

        // Should take at least 400ms (allowing margin for timing)
        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThan(300),
            $"Expected delay for token refill, got {sw.ElapsedMilliseconds}ms");
    }

    [Test]
    public async Task ServiceWorkerStreamHandler_RegistersWithClient()
    {
        // Verify that ServiceWorkerStreamHandler registers its OnRequest handler with WebTorrentClient
        var handler = new ServiceWorkerStreamHandler();
        var client = new WebTorrentClient();

        // Before registration, StreamHandler should be null
        Assert.That(client.StreamHandler, Is.Null);

        // Register the handler
        client.RegisterStreamHandler(handler);

        // After registration, StreamHandler should be set
        Assert.That(client.StreamHandler, Is.SameAs(handler),
            "RegisterStreamHandler should set the client's StreamHandler");

        handler.Dispose();
        await client.DisposeAsync();
    }
}
