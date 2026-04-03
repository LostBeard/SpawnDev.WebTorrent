using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Tests for the DisableWebSeeds feature.
/// Verifies that web seed downloads can be disabled globally and per-torrent,
/// forcing pieces to download only via WebRTC/TCP peers.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  DisableWebSeeds — Global Option
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task WebSeed_Disabled_Global_SkipsWebSeeds()
    {
        // Create client with web seeds disabled globally
        var options = new WebTorrentOptions { DisableWebSeeds = true };
        await using var client = new WebTorrentClient(crypto: Client!.Crypto, options: options);

        // Create a torrent from in-memory data (produces metadata with no web seeds)
        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("test-no-webseed.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        // Manually add a web seed URL to metadata to simulate a torrent that has them
        metadata.UrlList = new[] { "https://example.com/test-no-webseed.bin" };

        // Add the torrent — web seeds from metadata should be skipped
        var swarm = await client.AddAsync(metadata);

        // Verify: coordinator should have 0 web seeds because they were disabled
        if (swarm.Coordinator == null)
            throw new Exception("Coordinator should be created after metadata");
        if (swarm.Coordinator.WebSeedCount != 0)
            throw new Exception($"Expected 0 web seeds (disabled globally), got {swarm.Coordinator.WebSeedCount}");
        if (!swarm.WebSeedsDisabled)
            throw new Exception("WebSeedsDisabled should be true");
    }

    [TestMethod]
    public async Task WebSeed_Disabled_PerTorrent_SkipsWebSeeds()
    {
        // Create client with web seeds ENABLED globally
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);

        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("test-per-torrent.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });
        metadata.UrlList = new[] { "https://example.com/test-per-torrent.bin" };

        // Add with per-torrent DisableWebSeeds
        var swarm = await client.AddAsync(metadata, new AddTorrentOptions { DisableWebSeeds = true });

        if (swarm.Coordinator == null)
            throw new Exception("Coordinator should be created");
        if (swarm.Coordinator.WebSeedCount != 0)
            throw new Exception($"Expected 0 web seeds (disabled per-torrent), got {swarm.Coordinator.WebSeedCount}");
        if (!swarm.WebSeedsDisabled)
            throw new Exception("WebSeedsDisabled should be true");
    }

    [TestMethod]
    public async Task WebSeed_Enabled_Default_AddsWebSeeds()
    {
        // Create client with default options (web seeds enabled)
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);

        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("test-with-webseed.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });
        metadata.UrlList = new[] { "https://example.com/test-with-webseed.bin" };

        // Add with default options — web seeds should be added
        var swarm = await client.AddAsync(metadata);

        if (swarm.Coordinator == null)
            throw new Exception("Coordinator should be created");
        if (swarm.Coordinator.WebSeedCount != 1)
            throw new Exception($"Expected 1 web seed (enabled by default), got {swarm.Coordinator.WebSeedCount}");
        if (swarm.WebSeedsDisabled)
            throw new Exception("WebSeedsDisabled should be false by default");
    }
}
