using System.Security.Cryptography;
using System.Text;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 9 ut_metadata v2 extension tests. The community-standard extension adds a
/// `metadata_version: 2` key to the extended handshake when the peer is serving a v2
/// info dict. Both sides compare and drop to v1 if either side is only v1. Verification
/// switches from SHA-1 (v1) to SHA-256 (v2) when both sides agree on v2.
///
/// Two peers connected loopback-style (same pattern as wire-extension suite), exchange
/// real metadata dicts, verified via real hashes. No mocks.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task UtMetadataV2_Advertises_MetadataVersion2InHandshake()
    {
        var (peerA, peerB) = UtMetadataV2Tests_CreateConnectedPair();
        var realV2InfoDict = UtMetadataV2Tests_MakeFakeInfoDict(seed: 6101);
        var v2Hash = Convert.ToHexString(SHA256.HashData(realV2InfoDict)).ToLowerInvariant();

        // Seeder side: has the v2 info dict, advertises metadata_version=2
        var seedExt = new UtMetadataExtension(realV2InfoDict) { MetadataVersion = 2, V2InfoHashHex = v2Hash };
        peerA.Use(seedExt);
        seedExt.SetWire(peerA);

        // AutoFetchOnHandshake=false avoids a sync-loopback timing race where peerA
        // would try to respond before peerB's extended handshake had fully looped back.
        var fetchExt = new UtMetadataExtension() { MetadataVersion = 2, V2InfoHashHex = v2Hash, AutoFetchOnHandshake = false };
        peerB.Use(fetchExt);
        fetchExt.SetWire(peerB);

        await UtMetadataV2Tests_PerformHandshakes(peerA, peerB);
        for (int i = 0; i < 5; i++) await Task.Yield();

        // After BEP 10 handshake, each side's PeerExtendedHandshake holds the other's dict.
        if (peerB.PeerExtendedHandshake == null)
            throw new Exception("peerB.PeerExtendedHandshake is null");
        if (!peerB.PeerExtendedHandshake.TryGetValue("metadata_version", out var mvObj))
            throw new Exception($"peerB.PeerExtendedHandshake missing metadata_version; keys={string.Join(",", peerB.PeerExtendedHandshake.Keys)}");
        var mv = mvObj switch { long l => (int)l, int i => i, _ => 0 };
        if (mv != 2) throw new Exception($"peerB sees metadata_version={mv}, expected 2");

        if (fetchExt.PeerMetadataVersion != 2)
            throw new Exception($"fetchExt.PeerMetadataVersion={fetchExt.PeerMetadataVersion}, expected 2");
    }

    [TestMethod]
    public async Task UtMetadataV2_ExchangesV2InfoDict_VerifiesSha256()
    {
        // End-to-end: seeder has v2 info dict, fetcher requests it, receives pieces,
        // verifies against SHA-256(info_dict) == declared v2 hash.
        var (peerA, peerB) = UtMetadataV2Tests_CreateConnectedPair();
        var realV2InfoDict = UtMetadataV2Tests_MakeFakeInfoDict(seed: 6102);
        var v2Hash = Convert.ToHexString(SHA256.HashData(realV2InfoDict)).ToLowerInvariant();

        var seedExt = new UtMetadataExtension(realV2InfoDict) { MetadataVersion = 2, V2InfoHashHex = v2Hash };
        peerA.Use(seedExt);
        seedExt.SetWire(peerA);

        byte[]? received = null;
        // AutoFetchOnHandshake=false: sync loopback compresses the send chain, so if
        // OnExtendedHandshake auto-requested, peerA would try to respond before peerB's
        // extended handshake had fully looped back. Deferring keeps the test deterministic
        // on sync transports — in real async transports, auto-fetch is fine.
        var fetchExt = new UtMetadataExtension() { MetadataVersion = 2, V2InfoHashHex = v2Hash, AutoFetchOnHandshake = false };
        fetchExt.OnMetadata += bytes => received = bytes;
        peerB.Use(fetchExt);
        fetchExt.SetWire(peerB);

        await UtMetadataV2Tests_PerformHandshakes(peerA, peerB);
        // Let both sides' extended handshakes fully exchange.
        for (int i = 0; i < 5; i++) await Task.Yield();

        // Now explicitly start the fetch — peerA has peerB's extended mapping by now.
        fetchExt.Fetch();
        for (int i = 0; i < 5; i++) await Task.Yield();

        if (received is null)
            throw new Exception("fetchExt.OnMetadata never fired — v2 metadata exchange didn't complete");
        if (!received.SequenceEqual(realV2InfoDict))
            throw new Exception("received bytes do not match seeded v2 info dict");

        // Also verify SHA-256(received) == declared v2 hash
        var hashOfReceived = Convert.ToHexString(SHA256.HashData(received)).ToLowerInvariant();
        if (hashOfReceived != v2Hash)
            throw new Exception($"SHA-256 of received={hashOfReceived} != v2Hash={v2Hash}");
    }

    [TestMethod]
    public async Task UtMetadataV2_VerifyFailsOnTamperedBytes()
    {
        // SetMetadata in v2 mode with a bad payload should fail SHA-256 check and return false.
        var realV2InfoDict = UtMetadataV2Tests_MakeFakeInfoDict(seed: 6103);
        var v2Hash = Convert.ToHexString(SHA256.HashData(realV2InfoDict)).ToLowerInvariant();

        var ext = new UtMetadataExtension() { MetadataVersion = 2, V2InfoHashHex = v2Hash };

        // Tamper: flip one byte
        var tampered = (byte[])realV2InfoDict.Clone();
        tampered[0] ^= 0xFF;

        var ok = await ext.SetMetadata(tampered);
        if (ok) throw new Exception("SetMetadata must reject tampered v2 metadata (hash mismatch)");
    }

    [TestMethod]
    public async Task UtMetadataV2_VerifyPassesOnCleanBytes()
    {
        var realV2InfoDict = UtMetadataV2Tests_MakeFakeInfoDict(seed: 6104);
        var v2Hash = Convert.ToHexString(SHA256.HashData(realV2InfoDict)).ToLowerInvariant();

        var ext = new UtMetadataExtension() { MetadataVersion = 2, V2InfoHashHex = v2Hash };

        var ok = await ext.SetMetadata(realV2InfoDict);
        if (!ok) throw new Exception("SetMetadata must accept clean v2 metadata");
    }

    [TestMethod]
    public async Task UtMetadataV2_V1PeerMismatch_Skipped()
    {
        // We're v2, peer only advertises v1 (no metadata_version key). Extension warns and
        // does not start fetching, as requesting would produce a v1 dict that fails SHA-256.
        var (peerA, peerB) = UtMetadataV2Tests_CreateConnectedPair();
        var realV2InfoDict = UtMetadataV2Tests_MakeFakeInfoDict(seed: 6105);
        var v2Hash = Convert.ToHexString(SHA256.HashData(realV2InfoDict)).ToLowerInvariant();

        // Seeder is v1-only (default MetadataVersion = 1)
        var v1Seed = new UtMetadataExtension(realV2InfoDict); // no MetadataVersion set
        peerA.Use(v1Seed);
        v1Seed.SetWire(peerA);

        // Fetcher is v2
        var fetch = new UtMetadataExtension() { MetadataVersion = 2, V2InfoHashHex = v2Hash };
        string? warning = null;
        fetch.OnWarning += msg => warning = msg;
        byte[]? received = null;
        fetch.OnMetadata += bytes => received = bytes;
        peerB.Use(fetch);
        fetch.SetWire(peerB);

        await UtMetadataV2Tests_PerformHandshakes(peerA, peerB);
        await Task.Delay(300);

        // Fetcher should have warned and NOT received anything (v1 seed's info dict
        // would fail our v2 SHA-256 verification anyway).
        if (received is not null)
            throw new Exception("fetcher must not receive metadata from v1 peer when we are v2");
        if (fetch.PeerMetadataVersion != 1)
            throw new Exception($"fetcher should see peer as v1, got PeerMetadataVersion={fetch.PeerMetadataVersion}");
    }

    [TestMethod]
    public async Task UtMetadataV2_V1Path_Unchanged()
    {
        // Default path (MetadataVersion=1) must still work v1-to-v1 with SHA-1 verification.
        var (peerA, peerB) = UtMetadataV2Tests_CreateConnectedPair();
        var fakeV1InfoDict = UtMetadataV2Tests_MakeFakeInfoDict(seed: 6106);
        var v1Hash = Convert.ToHexString(SHA1.HashData(fakeV1InfoDict)).ToLowerInvariant();

        var seedExt = new UtMetadataExtension(fakeV1InfoDict); // default v1
        peerA.Use(seedExt);
        seedExt.SetWire(peerA);

        byte[]? received = null;
        var fetchExt = new UtMetadataExtension() { AutoFetchOnHandshake = false };
        fetchExt.OnMetadata += bytes => received = bytes;
        peerB.Use(fetchExt);
        fetchExt.SetWire(peerB);

        // Fetcher needs _infoHash set via the BT handshake to verify v1 via SHA-1.
        var v1HashBytes = Convert.FromHexString(v1Hash);
        var peerIdA = new byte[20]; for (int i = 0; i < 20; i++) peerIdA[i] = (byte)('A' + i);
        var peerIdB = new byte[20]; for (int i = 0; i < 20; i++) peerIdB[i] = (byte)('a' + i);
        await peerA.Handshake(v1HashBytes, peerIdA);
        await peerB.Handshake(v1HashBytes, peerIdB);
        for (int i = 0; i < 5; i++) await Task.Yield();

        fetchExt.Fetch();
        for (int i = 0; i < 5; i++) await Task.Yield();

        if (received is null)
            throw new Exception("v1 ut_metadata OnMetadata never fired");
        if (!received.SequenceEqual(fakeV1InfoDict))
            throw new Exception("v1 ut_metadata bytes don't match seeded dict");
    }

    // ---- helpers ----

    private static (Wire a, Wire b) UtMetadataV2Tests_CreateConnectedPair()
    {
        var a = new Wire();
        var b = new Wire();
        a.SendRaw = data => { b.DataReceived(data); return Task.CompletedTask; };
        b.SendRaw = data => { a.DataReceived(data); return Task.CompletedTask; };
        return (a, b);
    }

    private static async Task UtMetadataV2Tests_PerformHandshakes(Wire a, Wire b)
    {
        var infoHash = new byte[20];
        var peerIdA = new byte[20]; for (int i = 0; i < 20; i++) peerIdA[i] = (byte)('A' + i);
        var peerIdB = new byte[20]; for (int i = 0; i < 20; i++) peerIdB[i] = (byte)('a' + i);
        await a.Handshake(infoHash, peerIdA);
        await b.Handshake(infoHash, peerIdB);
    }

    /// <summary>
    /// Make a "fake" info dict — just deterministic bytes. The extension doesn't care
    /// about the internal structure; it only exchanges bytes and verifies the hash.
    /// </summary>
    private static byte[] UtMetadataV2Tests_MakeFakeInfoDict(int seed)
    {
        var data = new byte[20_000]; // ~1.2 pieces at 16 KiB — exercises multi-piece path
        new Random(seed).NextBytes(data);
        return data;
    }
}
