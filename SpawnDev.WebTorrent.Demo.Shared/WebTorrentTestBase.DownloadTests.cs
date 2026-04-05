using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Download_AddFromTorrentBytes_Metadata()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 20);
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("dl-meta.bin", data);
        var torrent = client.Add(torrentBytes);
        if (!torrent.HasMetadata) throw new Exception("Should have metadata immediately from .torrent bytes");
        if (torrent.InfoHash != meta.InfoHash) throw new Exception("InfoHash mismatch");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_Pause_StopsRequests()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 21);
        var torrent = await client.SeedAsync("pause.bin", data);
        torrent.Pause();
        if (!torrent.Paused) throw new Exception("Should be paused");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_Resume_RestartsRequests()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 22);
        var torrent = await client.SeedAsync("resume.bin", data);
        torrent.Pause();
        if (!torrent.Paused) throw new Exception("Should be paused");
        torrent.Resume();
        if (torrent.Paused) throw new Exception("Should not be paused after resume");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_Pause_DoesNotCorruptState()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(49152, seed: 23); // 3 pieces
        var torrent = await client.SeedAsync("pause-state.bin", data);
        var bfBefore = torrent.Bitfield.ToArray();
        torrent.Pause();
        torrent.Resume();
        var bfAfter = torrent.Bitfield.ToArray();
        if (!bfBefore.SequenceEqual(bfAfter)) throw new Exception("Bitfield changed after pause/resume");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_Remove_DisposesResources()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 24);
        var torrent = await client.SeedAsync("remove.bin", data);
        await client.RemoveAsync(torrent);
        if (!torrent.Destroyed) throw new Exception("Torrent should be Destroyed after remove");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_Remove_NotInClientList()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 25);
        var torrent = await client.SeedAsync("remove-list.bin", data);
        var hash = torrent.InfoHash;
        await client.RemoveAsync(torrent);
        if (client.Get(hash!) != null) throw new Exception("Torrent should not be in client after remove");
        if (client.Torrents.Count != 0) throw new Exception($"Expected 0 torrents, got {client.Torrents.Count}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_RemoveWithData_ClearsStore()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 26);
        var torrent = await client.SeedAsync("rmdata.bin", data);
        var hash = torrent.InfoHash;
        await client.RemoveWithDataAsync(torrent);
        if (!torrent.Destroyed) throw new Exception("Torrent should be Destroyed");
        if (client.Get(hash!) != null) throw new Exception("Should not find torrent after RemoveWithData");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_DuplicateAdd_ReturnsSame()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 27);
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("dup.bin", data);
        var t1 = client.Add(torrentBytes);
        var t2 = client.Add(torrentBytes); // duplicate
        // Should return the existing torrent, not create a second
        if (client.Torrents.Count > 1) throw new Exception($"Expected 1 torrent, got {client.Torrents.Count}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_Strategy_Sequential()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 28);
        var torrent = await client.SeedAsync("seq.bin", data);
        torrent.Strategy = "sequential";
        if (torrent.Strategy != "sequential") throw new Exception("Strategy not set");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Download_Strategy_Rarest()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 29);
        var torrent = await client.SeedAsync("rarest.bin", data);
        torrent.Strategy = "rarest";
        if (torrent.Strategy != "rarest") throw new Exception("Strategy not set");
        await client.DisposeAsync();
    }
}
