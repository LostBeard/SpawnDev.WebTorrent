using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 magnet URI tests - Phase 2c step 1.
/// v1 magnet: magnet:?xt=urn:btih:&lt;40-hex-sha1&gt;
/// v2 magnet: magnet:?xt=urn:btmh:1220&lt;64-hex-sha256&gt; (multihash prefix 1220 = SHA-256/32B)
/// Hybrid: both xt parameters present.
/// Migrated from NUnit MagnetUriV2Tests.cs.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private const string MagnetV1Hash = "aaaabbbbccccddddeeeeffff1111222233334444";
    private const string MagnetV2Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public async Task Magnet_ParseV1Only_PopulatesInfoHashNotV2()
    {
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btih:{MagnetV1Hash}&dn=x&tr=http://tracker.example/");

        if (t.InfoHash != MagnetV1Hash) throw new Exception($"InfoHash={t.InfoHash}, expected {MagnetV1Hash}");
        if (!string.IsNullOrEmpty(t.V2InfoHash)) throw new Exception($"v1-only magnet must leave V2InfoHash empty, got {t.V2InfoHash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ParseV2Only_PopulatesV2InfoHashNotV1()
    {
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1220{MagnetV2Hash}");

        if (!string.IsNullOrEmpty(t.InfoHash)) throw new Exception($"v2-only magnet must leave InfoHash empty, got {t.InfoHash}");
        if (t.V2InfoHash != MagnetV2Hash) throw new Exception($"V2InfoHash={t.V2InfoHash}, expected {MagnetV2Hash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ParseHybrid_PopulatesBoth()
    {
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btih:{MagnetV1Hash}&xt=urn:btmh:1220{MagnetV2Hash}");

        if (t.InfoHash != MagnetV1Hash) throw new Exception($"InfoHash={t.InfoHash}, expected {MagnetV1Hash}");
        if (t.V2InfoHash != MagnetV2Hash) throw new Exception($"V2InfoHash={t.V2InfoHash}, expected {MagnetV2Hash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ParseV2_NonSha256Multihash_Ignored()
    {
        // Prefix 1240 is not SHA-256. BEP 52 mandates SHA-256; parser must reject.
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1240{MagnetV2Hash}00");

        if (!string.IsNullOrEmpty(t.V2InfoHash))
            throw new Exception($"non-SHA-256 multihash prefix must be rejected, got V2InfoHash={t.V2InfoHash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ParseV2_WrongLength_Ignored()
    {
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1220{MagnetV2Hash[..62]}"); // 62 hex = 31 bytes, short by 1
        if (!string.IsNullOrEmpty(t.V2InfoHash))
            throw new Exception($"wrong-length multihash must be rejected, got V2InfoHash={t.V2InfoHash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ParseLowercasesV2Hash()
    {
        var upper = MagnetV2Hash.ToUpperInvariant();
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1220{upper}");

        if (t.V2InfoHash != MagnetV2Hash)
            throw new Exception($"V2 hash must be normalized to lowercase, got {t.V2InfoHash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ComputedUri_V2Only_EmitsBtmhOnly()
    {
        var t = new Torrent { V2InfoHash = MagnetV2Hash, Name = "test.bin" };
        var uri = t.ComputedMagnetUri;

        if (!uri.Contains($"urn:btmh:1220{MagnetV2Hash}")) throw new Exception($"v2-only magnet must include btmh, got {uri}");
        if (uri.Contains("urn:btih:")) throw new Exception($"v2-only magnet must NOT include btih, got {uri}");
        if (!uri.Contains("dn=test.bin")) throw new Exception($"v2-only magnet should contain display name, got {uri}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ComputedUri_V1Only_EmitsBtihOnly()
    {
        var t = new Torrent { InfoHash = MagnetV1Hash };
        var uri = t.ComputedMagnetUri;

        if (!uri.Contains($"urn:btih:{MagnetV1Hash}")) throw new Exception($"v1-only magnet must include btih, got {uri}");
        if (uri.Contains("urn:btmh:")) throw new Exception($"v1-only magnet must NOT include btmh, got {uri}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ComputedUri_Hybrid_EmitsBoth()
    {
        var t = new Torrent { InfoHash = MagnetV1Hash, V2InfoHash = MagnetV2Hash };
        var uri = t.ComputedMagnetUri;

        if (!uri.Contains($"urn:btih:{MagnetV1Hash}")) throw new Exception($"hybrid must include btih, got {uri}");
        if (!uri.Contains($"urn:btmh:1220{MagnetV2Hash}")) throw new Exception($"hybrid must include btmh, got {uri}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_ComputedUri_NoHashes_ReturnsEmpty()
    {
        var t = new Torrent();
        if (!string.IsNullOrEmpty(t.ComputedMagnetUri))
            throw new Exception($"no hashes => empty magnet URI, got '{t.ComputedMagnetUri}'");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_RoundTrip_HybridPreservesBothHashes()
    {
        var source = new Torrent { InfoHash = MagnetV1Hash, V2InfoHash = MagnetV2Hash };
        var uri = source.ComputedMagnetUri;

        var parsed = new Torrent();
        parsed.ParseMagnet(uri);

        if (parsed.InfoHash != MagnetV1Hash) throw new Exception($"round-trip InfoHash={parsed.InfoHash}, expected {MagnetV1Hash}");
        if (parsed.V2InfoHash != MagnetV2Hash) throw new Exception($"round-trip V2InfoHash={parsed.V2InfoHash}, expected {MagnetV2Hash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Magnet_FromHybridTorrent_RealInfoHashes()
    {
        // End-to-end: real hybrid torrent -> magnet -> parse -> both hashes survive.
        var data = new byte[50000];
        new Random(710).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };
        var (_, created) = TorrentCreator.CreateFromBytes("real.bin", data, opts);

        var magnetTorrent = new Torrent
        {
            InfoHash = created.InfoHash,
            V2InfoHash = created.V2InfoHash,
            Name = created.Name,
        };
        var uri = magnetTorrent.ComputedMagnetUri;

        var parsed = new Torrent();
        parsed.ParseMagnet(uri);
        if (parsed.InfoHash != created.InfoHash)
            throw new Exception($"real magnet round-trip InfoHash={parsed.InfoHash}, expected {created.InfoHash}");
        if (parsed.V2InfoHash != created.V2InfoHash)
            throw new Exception($"real magnet round-trip V2InfoHash={parsed.V2InfoHash}, expected {created.V2InfoHash}");
        await Task.CompletedTask;
    }
}
