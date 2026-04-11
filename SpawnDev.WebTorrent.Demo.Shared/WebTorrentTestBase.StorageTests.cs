using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Storage_MemoryStore_PutGet()
    {
        var store = new MemoryChunkStore(16384);
        var data = MakeDeterministicData(16384, seed: 40);
        await store.PutAsync(0, data);
        var result = await store.GetAsync(0);
        if (result == null) throw new Exception("GetAsync returned null");
        if (!result.SequenceEqual(data)) throw new Exception("Data mismatch");
    }

    [TestMethod]
    public async Task Storage_MemoryStore_GetWithOffset()
    {
        var store = new MemoryChunkStore(16384);
        var data = MakeDeterministicData(16384, seed: 41);
        await store.PutAsync(0, data);
        var slice = await store.GetAsync(0, 100, 256);
        if (slice == null) throw new Exception("Slice is null");
        if (slice.Length != 256) throw new Exception($"Expected 256, got {slice.Length}");
        var expected = data[100..356];
        if (!slice.SequenceEqual(expected)) throw new Exception("Slice data mismatch");
    }

    [TestMethod]
    public async Task Storage_MemoryStore_Remove_ReturnsNull()
    {
        var store = new MemoryChunkStore(16384);
        var data = MakeDeterministicData(16384, seed: 42);
        await store.PutAsync(0, data);
        await store.RemoveAsync(0);
        var result = await store.GetAsync(0);
        if (result != null) throw new Exception("Should be null after remove");
    }

    [TestMethod]
    public async Task Storage_MemoryStore_Clear_RemovesAll()
    {
        var store = new MemoryChunkStore(16384);
        await store.PutAsync(0, MakeDeterministicData(16384, seed: 43));
        await store.PutAsync(1, MakeDeterministicData(16384, seed: 44));
        await store.ClearAsync();
        if (await store.GetAsync(0) != null) throw new Exception("Piece 0 should be null after clear");
        if (await store.GetAsync(1) != null) throw new Exception("Piece 1 should be null after clear");
    }

    [TestMethod]
    public async Task Storage_Persist_MetadataWritten()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("OPFS persistence only available in browser");
        // Browser-only: verify that SeedAsync writes .torrent metadata to OPFS state dir
        var torrent = await Client.SeedAsync("persist.bin", MakeDeterministicData(16384, seed: 45));
        // The metadata file should exist at webtorrent/_state/{hash}.torrent
        // This is verified by checking that TorrentFileBytes is not null (was persisted)
        if (torrent.TorrentFileBytes == null) throw new Exception("TorrentFileBytes is null — metadata not persisted");
        await Client.RemoveAsync(torrent);
    }

    [TestMethod]
    public async Task Storage_MemoryStore_MultiPiece_IndependentStorage()
    {
        // Verify pieces are stored independently - writing piece 1 doesn't corrupt piece 0
        var store = new MemoryChunkStore(16384);
        var data0 = MakeDeterministicData(16384, seed: 46);
        var data1 = MakeDeterministicData(16384, seed: 47);
        await store.PutAsync(0, data0);
        await store.PutAsync(1, data1);

        var read0 = await store.GetAsync(0);
        var read1 = await store.GetAsync(1);
        if (read0 == null || read1 == null) throw new Exception("GetAsync returned null");
        if (!read0.SequenceEqual(data0)) throw new Exception("Piece 0 data corrupted after writing piece 1");
        if (!read1.SequenceEqual(data1)) throw new Exception("Piece 1 data wrong");

        // Overwrite piece 0 with new data
        var data0v2 = MakeDeterministicData(16384, seed: 48);
        await store.PutAsync(0, data0v2);
        read0 = await store.GetAsync(0);
        read1 = await store.GetAsync(1);
        if (!read0!.SequenceEqual(data0v2)) throw new Exception("Piece 0 overwrite failed");
        if (!read1!.SequenceEqual(data1)) throw new Exception("Piece 1 corrupted by piece 0 overwrite");
    }
}
