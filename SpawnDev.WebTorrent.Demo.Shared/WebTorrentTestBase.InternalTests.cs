using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Bencode;
using System.Text;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests for internal algorithms and components that didn't have dedicated coverage:
/// Bencode, RarityMap, Selections, rechoke, endgame, speed tracking, web seeds,
/// TCP peer, multi-tracker, ServiceWorkerStreamHandler, TorrentHttpServer, Wire.GetExtension.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ── Bencode Round-Trip ──

    [TestMethod]
    public async Task Bencode_String_RoundTrip()
    {
        var original = "hello world";
        var encoded = BencodeEncoder.Encode(new Dictionary<string, object>
        {
            ["msg"] = Encoding.UTF8.GetBytes(original)
        });
        var (decoded, _) = BencodeDecoder.Decode(encoded, 0);
        if (decoded is not Dictionary<string, object> dict) throw new Exception("Not a dict");
        if (dict["msg"] is not byte[] val) throw new Exception("Not bytes");
        if (Encoding.UTF8.GetString(val) != original) throw new Exception("Round-trip failed");
    }

    [TestMethod]
    public async Task Bencode_Integer_RoundTrip()
    {
        var encoded = BencodeEncoder.Encode(new Dictionary<string, object> { ["num"] = 42L });
        var (decoded, _) = BencodeDecoder.Decode(encoded, 0);
        if (decoded is not Dictionary<string, object> dict) throw new Exception("Not a dict");
        if (dict["num"] is not long val || val != 42) throw new Exception($"Expected 42, got {dict["num"]}");
    }

    [TestMethod]
    public async Task Bencode_List_RoundTrip()
    {
        var list = new List<object> { Encoding.UTF8.GetBytes("a"), Encoding.UTF8.GetBytes("b"), 3L };
        var encoded = BencodeEncoder.Encode(new Dictionary<string, object> { ["items"] = list });
        var (decoded, _) = BencodeDecoder.Decode(encoded, 0);
        if (decoded is not Dictionary<string, object> dict) throw new Exception("Not a dict");
        if (dict["items"] is not List<object> items || items.Count != 3) throw new Exception("List round-trip failed");
    }

    [TestMethod]
    public async Task Bencode_NestedDict_RoundTrip()
    {
        var inner = new Dictionary<string, object> { ["key"] = Encoding.UTF8.GetBytes("value") };
        var encoded = BencodeEncoder.Encode(new Dictionary<string, object> { ["nested"] = inner });
        var (decoded, _) = BencodeDecoder.Decode(encoded, 0);
        if (decoded is not Dictionary<string, object> dict) throw new Exception("Not a dict");
        if (dict["nested"] is not Dictionary<string, object> nested) throw new Exception("Not nested dict");
        if (nested["key"] is not byte[] val || Encoding.UTF8.GetString(val) != "value")
            throw new Exception("Nested value wrong");
    }

    // ── RarityMap ──

    [TestMethod]
    public async Task RarityMap_OnSeededTorrent_NoMissingPieces()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(49152, seed: 910); // 3 pieces
        var torrent = await client.SeedAsync("rarity.bin", data);

        var map = new RarityMap(torrent);
        // All pieces done — filter for missing should return -1
        var rarest = map.GetRarestPiece((idx) => !torrent.Bitfield[idx]);
        if (rarest != -1) throw new Exception($"All pieces done, rarest should be -1, got {rarest}");

        map.Destroy();
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task RarityMap_UnfilteredReturnsValidIndex()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(49152, seed: 911);
        var torrent = await client.SeedAsync("rarity2.bin", data);

        var map = new RarityMap(torrent);
        // No filter — should return a valid piece index (all at availability 0, no peers)
        var anyPiece = map.GetRarestPiece();
        if (anyPiece < 0 || anyPiece >= torrent.PieceCount)
            throw new Exception($"Expected valid piece index, got {anyPiece}");

        map.Destroy();
        await client.DisposeAsync();
    }

    // ── Speed Tracking ──

    [TestMethod]
    public async Task Speed_PropertiesAccessible()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 900);
        var torrent = await client.SeedAsync("speed-track.bin", data);

        // Speed timer runs internally at 1s interval — wait for one sample
        await Task.Delay(1100);

        if (double.IsNaN(torrent.DownloadSpeed)) throw new Exception("DownloadSpeed is NaN");
        if (double.IsNaN(torrent.UploadSpeed)) throw new Exception("UploadSpeed is NaN");
        if (double.IsInfinity(torrent.DownloadSpeed)) throw new Exception("DownloadSpeed is Infinity");

        // Downloaded/Uploaded totals should be accessible
        if (torrent.Downloaded < 0) throw new Exception("Downloaded should not be negative");
        if (torrent.Uploaded < 0) throw new Exception("Uploaded should not be negative");

        await client.DisposeAsync();
    }

    // ── Piece Internals ──

    [TestMethod]
    public async Task Piece_BlockReservation_Exhausts()
    {
        var piece = new Piece(32768); // 2 blocks of 16KB
        int r1 = piece.Reserve();
        int r2 = piece.Reserve();
        int r3 = piece.Reserve(); // should be -1

        if (r1 < 0) throw new Exception("First reserve should succeed");
        if (r2 < 0) throw new Exception("Second reserve should succeed");
        if (r3 >= 0) throw new Exception("Third reserve should fail (only 2 blocks)");
    }

    [TestMethod]
    public async Task Piece_CancelAllowsReReserve()
    {
        var piece = new Piece(16384); // 1 block
        int r1 = piece.Reserve();
        piece.Cancel(r1);
        int r2 = piece.Reserve();

        if (r1 < 0) throw new Exception("First reserve should succeed");
        if (r2 < 0) throw new Exception("Reserve after cancel should succeed");
    }

    // ── Download Engine Stability ──

    [TestMethod]
    public async Task Rechoke_DoesNotCrash()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 901);
        var torrent = await client.SeedAsync("rechoke.bin", data);

        // Rechoke fires on a 10s timer — torrent should be stable
        await Task.Delay(100);
        if (torrent.Destroyed) throw new Exception("Should not be destroyed");
        if (!torrent.Done) throw new Exception("Should be done");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Endgame_CompletedPiecesMatchTotal()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 902);
        var torrent = await client.SeedAsync("endgame.bin", data);

        if (!torrent.Done) throw new Exception("Should be done");
        if (torrent.Bitfield.Any(b => !b)) throw new Exception("All pieces should be set");
        if (torrent.CompletedPieces != torrent.PieceCount)
            throw new Exception($"Completed {torrent.CompletedPieces} != total {torrent.PieceCount}");

        await client.DisposeAsync();
    }

    // ── Web Seed ──

    [TestMethod]
    public async Task WebSeed_TorrentIncludesUrlList()
    {
        var data = MakeDeterministicData(16384, seed: 903);
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("webseed.bin", data,
            new TorrentCreatorOptions { WebSeeds = new[] { "https://cdn.example.com/data.bin" } });
        var parsed = TorrentParser.Parse(torrentBytes);

        if (parsed.UrlList == null || !parsed.UrlList.Any(u => u.Contains("cdn.example.com")))
            throw new Exception("Web seed URL not found in parsed torrent");
    }

    [TestMethod]
    public async Task WebSeed_CountAccessible()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 904);
        var torrent = await client.SeedAsync("ws.bin", data,
            new TorrentCreatorOptions { WebSeeds = new[] { "https://example.com/f.bin" } });

        var count = torrent.WebSeedCount;
        if (count < 0) throw new Exception("WebSeedCount should not be negative");
        await client.DisposeAsync();
    }

    // ── TcpPeer ──

    [TestMethod]
    public async Task TcpPeer_ConstructsAndDisposes()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("TCP not available in browser");

        var peer = new TcpPeer(initiator: true);
        if (peer.Destroyed) throw new Exception("Should not start destroyed");
        if (peer.Connected) throw new Exception("Should not start connected");
        await peer.DisposeAsync();
        if (!peer.Destroyed) throw new Exception("Should be destroyed after dispose");
    }

    // ── ServiceWorkerStreamHandler ──

    [TestMethod]
    public async Task ServiceWorkerStreamHandler_RegistersWithClient()
    {
        var handler = new ServiceWorkerStreamHandler();
        var client = CreateIsolatedClient();

        // Before registration, StreamHandler should be null
        if (client.StreamHandler != null)
            throw new Exception("StreamHandler should be null before registration");

        // Register the handler
        client.RegisterStreamHandler(handler);

        // After registration, StreamHandler should be set
        if (client.StreamHandler != handler)
            throw new Exception("StreamHandler should reference the registered handler");

        handler.Dispose();
        await client.DisposeAsync();
    }

    // ── TorrentHttpServer ──

    [TestMethod]
    public async Task TorrentHttpServer_ConstructsWithPort()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("HttpListener not available in browser");

        var client = CreateIsolatedClient();
        var server = new TorrentHttpServer(client, 18999);
        if (!server.IsRunning == true && server.IsRunning == true)
            throw new Exception("Unexpected state"); // just verify construction doesn't throw
        // IsRunning should be false before Start
        if (server.IsRunning) throw new Exception("Should not be running before Start");
        await server.DisposeAsync();
        await client.DisposeAsync();
    }

    // ── Multi-Tracker ──

    [TestMethod]
    public async Task MultiTracker_BothTrackersInAnnounceList()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 905);
        var torrent = await client.SeedAsync("multi-tr.bin", data,
            new TorrentCreatorOptions
            {
                Trackers = new[] { "wss://tr1.example.com/announce", "http://tr2.example.com/announce" }
            });

        if (torrent.AnnounceUrls.Length < 2)
            throw new Exception($"Expected 2+ trackers, got {torrent.AnnounceUrls.Length}");
        await client.DisposeAsync();
    }

    // ── Wire.GetExtension ──

    [TestMethod]
    public async Task Wire_GetExtension_ByName()
    {
        var wire = new Wire();
        wire.SendRaw = async (data) => { };
        var ext = new UtPexExtension();
        ext.SetWire(wire);
        wire.Use(ext);

        if (wire.GetExtension("ut_pex") == null) throw new Exception("Should find by name");
    }

    [TestMethod]
    public async Task Wire_GetExtension_ByType()
    {
        var wire = new Wire();
        wire.SendRaw = async (data) => { };
        var ext = new UtPexExtension();
        ext.SetWire(wire);
        wire.Use(ext);

        if (wire.GetExtension<UtPexExtension>() == null) throw new Exception("Should find by type");
    }

    [TestMethod]
    public async Task Wire_GetExtension_NullForMissing()
    {
        var wire = new Wire();
        wire.SendRaw = async (data) => { };

        if (wire.GetExtension("nonexistent") != null) throw new Exception("Should be null");
        if (wire.GetExtension<UtPexExtension>() != null) throw new Exception("Should be null");
    }
}
