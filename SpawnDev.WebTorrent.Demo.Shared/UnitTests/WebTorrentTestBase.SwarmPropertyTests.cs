using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// TorrentSwarm property tests — verify every public property after seeding.
/// Ensures full API surface coverage for the primary user-facing object.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  Swarm Properties After Seed
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Swarm_Props_MetadataFields()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[50000];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "props-test.bin");

        if (swarm.CreatedBy != "SpawnDev.WebTorrent")
            throw new Exception($"CreatedBy: '{swarm.CreatedBy}'");
        if (swarm.IsPrivate)
            throw new Exception("Should not be private by default");
        if (swarm.PieceLength <= 0)
            throw new Exception($"PieceLength: {swarm.PieceLength}");
        if (swarm.Length != 50000)
            throw new Exception($"Length: {swarm.Length}");
    }

    [TestMethod]
    public async Task Swarm_Props_LastPieceLength()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        // 50000 bytes, default piece length 16384 → last piece = 50000 - 3*16384 = 848
        var data = new byte[50000];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "lastpiece.bin");

        if (swarm.LastPieceLength <= 0)
            throw new Exception($"LastPieceLength: {swarm.LastPieceLength}");
        if (swarm.LastPieceLength >= swarm.PieceLength)
            throw new Exception($"LastPieceLength ({swarm.LastPieceLength}) should be < PieceLength ({swarm.PieceLength}) for non-aligned data");
    }

    [TestMethod]
    public async Task Swarm_Props_Announce()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "announce.bin", new TorrentCreatorOptions
        {
            Trackers = new[] { "wss://tracker1.example.com", "wss://tracker2.example.com" }
        });

        var announce = swarm.Announce;
        if (announce == null || announce.Length < 2)
            throw new Exception($"Announce should have at least 2 trackers: {announce?.Length}");
    }

    [TestMethod]
    public async Task Swarm_Props_Bitfield_AfterSeed()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "bitfield.bin");

        var bitfield = swarm.Bitfield;
        if (bitfield == null)
            throw new Exception("Bitfield null after seed");
        if (!bitfield.All(b => b))
            throw new Exception("All bits should be set after seeding");
    }

    [TestMethod]
    public async Task Swarm_Props_StateAfterSeed()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "state.bin");

        if (!swarm.Done) throw new Exception("Should be done after seed");
        if (!swarm.Ready) throw new Exception("Should be ready after seed");
        if (!swarm.HasMetadata) throw new Exception("Should have metadata after seed");
        if (swarm.Paused) throw new Exception("Should not be paused after seed");
        if (swarm.Progress < 0.99) throw new Exception($"Progress: {swarm.Progress}");
    }

    [TestMethod]
    public async Task Swarm_Props_MagnetURI_Format()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "magnet-fmt.bin");

        var magnet = swarm.MagnetURI;
        if (!magnet.StartsWith("magnet:?xt=urn:btih:"))
            throw new Exception($"MagnetURI format wrong: {magnet[..30]}...");
        if (!magnet.Contains("dn=magnet-fmt.bin"))
            throw new Exception("MagnetURI should contain display name");
    }

    [TestMethod]
    public async Task Swarm_Props_TimeRemaining_AfterSeed()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "eta.bin");

        // Done torrents have 0 time remaining
        if (swarm.TimeRemaining != 0)
            throw new Exception($"TimeRemaining after seed should be 0: {swarm.TimeRemaining}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Swarm Files
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Swarm_Files_Count()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "files-count.bin");

        if (swarm.Files == null || swarm.Files.Length != 1)
            throw new Exception($"Files count: {swarm.Files?.Length}");
    }

    [TestMethod]
    public async Task Swarm_Files_Properties()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "file-props.bin");

        var file = swarm.Files[0];
        if (file.Name != "file-props.bin")
            throw new Exception($"File name: '{file.Name}'");
        if (file.Length != 32768)
            throw new Exception($"File length: {file.Length}");
        if (file.Path != "file-props.bin")
            throw new Exception($"File path: '{file.Path}'");
        if (!file.Done)
            throw new Exception("File should be done after seed");
        if (file.Progress < 0.99)
            throw new Exception($"File progress: {file.Progress}");
    }

    [TestMethod]
    public async Task Swarm_PrivateTorrent_Properties()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "private.bin", new TorrentCreatorOptions { IsPrivate = true });

        if (!swarm.IsPrivate)
            throw new Exception("Should be private");
    }
}
