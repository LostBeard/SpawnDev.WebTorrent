using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// BEP 52 magnet URI tests - Phase 2c step 1.
///
/// v1 magnet: <c>magnet:?xt=urn:btih:&lt;40-hex-sha1-or-32-base32&gt;</c>
/// v2 magnet: <c>magnet:?xt=urn:btmh:1220&lt;64-hex-sha256&gt;</c> (multihash prefix 1220 = SHA-256/32B)
/// Hybrid:     both xt parameters present.
/// </summary>
[TestFixture]
public class MagnetUriV2Tests
{
    private const string V1Hash = "aaaabbbbccccddddeeeeffff1111222233334444";           // 40 hex chars
    private const string V2Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"; // 64 hex chars

    [Test]
    public void ParseMagnet_V1Only_PopulatesInfoHashNotV2()
    {
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btih:{V1Hash}&dn=x&tr=http://tracker.example/");

        Assert.That(t.InfoHash, Is.EqualTo(V1Hash));
        Assert.That(t.V2InfoHash, Is.EqualTo(""), "v1-only magnet must not populate V2InfoHash");
    }

    [Test]
    public void ParseMagnet_V2Only_PopulatesV2InfoHashNotV1()
    {
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1220{V2Hash}");

        Assert.That(t.InfoHash, Is.Null.Or.Empty, "v2-only magnet must not populate InfoHash");
        Assert.That(t.V2InfoHash, Is.EqualTo(V2Hash));
    }

    [Test]
    public void ParseMagnet_Hybrid_PopulatesBoth()
    {
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btih:{V1Hash}&xt=urn:btmh:1220{V2Hash}");

        Assert.That(t.InfoHash, Is.EqualTo(V1Hash));
        Assert.That(t.V2InfoHash, Is.EqualTo(V2Hash));
    }

    [Test]
    public void ParseMagnet_V2_NonSha256Multihash_Ignored()
    {
        // Multihash prefix 1240 would signal SHA-512 (or something non-SHA-256). BEP 52
        // mandates SHA-256 only, so our parser must ignore anything else rather than
        // misinterpret the digest bytes.
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1240{V2Hash}00"); // 70 hex chars, wrong prefix

        Assert.That(t.V2InfoHash, Is.EqualTo(""), "Non-SHA-256 multihash prefix must be rejected");
    }

    [Test]
    public void ParseMagnet_V2_WrongLength_Ignored()
    {
        // Multihash prefix correct but digest is 31 bytes (62 hex chars) - malformed.
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1220{V2Hash[..62]}"); // 66 hex chars total

        Assert.That(t.V2InfoHash, Is.EqualTo(""));
    }

    [Test]
    public void ParseMagnet_LowercasesV2Hash()
    {
        var upper = V2Hash.ToUpperInvariant();
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1220{upper}");

        Assert.That(t.V2InfoHash, Is.EqualTo(V2Hash), "V2 hash must be normalized to lowercase");
    }

    [Test]
    public void ComputedMagnetUri_V2Only_EmitsBtmhOnly()
    {
        var t = new Torrent { V2InfoHash = V2Hash, Name = "test.bin" };
        var uri = t.ComputedMagnetUri;

        Assert.That(uri, Does.Contain($"urn:btmh:1220{V2Hash}"));
        Assert.That(uri, Does.Not.Contain("urn:btih:"));
        Assert.That(uri, Does.Contain("dn=test.bin"));
    }

    [Test]
    public void ComputedMagnetUri_V1Only_EmitsBtihOnly()
    {
        var t = new Torrent { InfoHash = V1Hash };
        var uri = t.ComputedMagnetUri;

        Assert.That(uri, Does.Contain($"urn:btih:{V1Hash}"));
        Assert.That(uri, Does.Not.Contain("urn:btmh:"));
    }

    [Test]
    public void ComputedMagnetUri_Hybrid_EmitsBoth()
    {
        var t = new Torrent { InfoHash = V1Hash, V2InfoHash = V2Hash };
        var uri = t.ComputedMagnetUri;

        Assert.That(uri, Does.Contain($"urn:btih:{V1Hash}"));
        Assert.That(uri, Does.Contain($"urn:btmh:1220{V2Hash}"));
    }

    [Test]
    public void ComputedMagnetUri_NoHashes_ReturnsEmpty()
    {
        var t = new Torrent();
        Assert.That(t.ComputedMagnetUri, Is.EqualTo(""));
    }

    [Test]
    public void Magnet_RoundTrip_HybridPreservesBothHashes()
    {
        // Build a hybrid magnet via ComputedMagnetUri, parse it back, assert both hashes
        // survive intact.
        var source = new Torrent { InfoHash = V1Hash, V2InfoHash = V2Hash };
        var uri = source.ComputedMagnetUri;

        var parsed = new Torrent();
        parsed.ParseMagnet(uri);

        Assert.That(parsed.InfoHash, Is.EqualTo(V1Hash));
        Assert.That(parsed.V2InfoHash, Is.EqualTo(V2Hash));
    }

    [Test]
    public void Magnet_FromHybridTorrent_RealInfoHashes()
    {
        // End-to-end: create a real hybrid torrent via TorrentCreator, surface the magnet
        // URI, parse it, assert both infohashes round-trip. Exercises the magnet emission
        // and parsing against actual hashes (not hand-rolled test vectors).
        var data = new byte[50000];
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };
        var (_, created) = TorrentCreator.CreateFromBytes("real.bin", data, opts);

        // Build a magnet string manually (the creator doesn't auto-emit one; Torrent does).
        var magnetTorrent = new Torrent
        {
            InfoHash = created.InfoHash,
            V2InfoHash = created.V2InfoHash,
            Name = created.Name,
        };
        var uri = magnetTorrent.ComputedMagnetUri;

        var parsed = new Torrent();
        parsed.ParseMagnet(uri);
        Assert.That(parsed.InfoHash, Is.EqualTo(created.InfoHash));
        Assert.That(parsed.V2InfoHash, Is.EqualTo(created.V2InfoHash));
    }
}
