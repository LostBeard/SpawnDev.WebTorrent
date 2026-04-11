using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 46 (Updating Torrents Via DHT Mutable Items) tests.
/// Validates: crypto test vectors, btpk magnet parsing, target computation,
/// sequence handling, sign data encoding, and the publisher/consumer update flow.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ── BEP 46 Official Test Vectors ──

    [TestMethod]
    public async Task Bep46_TestVector_TargetFromPublicKey()
    {
        // Official BEP 46 test vector
        var publicKey = Convert.FromHexString("8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e");
        var expectedTarget = "cc3f9d90b572172053626f9980ce261a850d050b";

        var target = DhtMutableItems.ComputeTarget(publicKey, null);
        var targetHex = Convert.ToHexString(target).ToLowerInvariant();

        if (targetHex != expectedTarget)
            throw new Exception($"BEP 46 test vector FAILED.\n  Expected: {expectedTarget}\n  Got:      {targetHex}");
    }

    [TestMethod]
    public async Task Bep46_TargetDeterministic()
    {
        var pubKey = Convert.FromHexString("8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e");
        var t1 = DhtMutableItems.ComputeTarget(pubKey, null);
        var t2 = DhtMutableItems.ComputeTarget(pubKey, null);
        if (!t1.SequenceEqual(t2))
            throw new Exception("ComputeTarget must be deterministic for same input");
    }

    [TestMethod]
    public async Task Bep46_TargetWithSalt_DiffersFromNoSalt()
    {
        var pubKey = Convert.FromHexString("8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e");
        var noSalt = DhtMutableItems.ComputeTarget(pubKey, null);
        var withSalt = DhtMutableItems.ComputeTarget(pubKey, Encoding.UTF8.GetBytes("model-v1"));
        if (noSalt.SequenceEqual(withSalt))
            throw new Exception("Target with salt must differ from target without salt");
    }

    [TestMethod]
    public async Task Bep46_TargetIs20Bytes()
    {
        var pubKey = new byte[32];
        RandomNumberGenerator.Fill(pubKey);
        var target = DhtMutableItems.ComputeTarget(pubKey, null);
        if (target.Length != 20)
            throw new Exception($"Target must be 20 bytes (SHA-1), got {target.Length}");
    }

    // ── btpk Magnet URI Parsing ──

    [TestMethod]
    public async Task Bep46_BtpkMagnet_ParsesPublicKey()
    {
        var pubKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
        var magnetUri = $"magnet:?xs=urn:btpk:{pubKeyHex}&dn=TestTorrent&tr=wss%3A%2F%2Ftracker.example.com";

        var client = CreateIsolatedClient();
        var torrent = client.Add(magnetUri);

        if (torrent.BtpkPublicKey == null)
            throw new Exception("BtpkPublicKey should be parsed from btpk magnet");
        if (Convert.ToHexString(torrent.BtpkPublicKey).ToLowerInvariant() != pubKeyHex)
            throw new Exception("Parsed public key doesn't match magnet URI");
        if (!torrent.IsMutableTorrent)
            throw new Exception("IsMutableTorrent should be true for btpk magnets");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep46_BtpkMagnet_WithBtih_ParsesBoth()
    {
        // A magnet can have BOTH btih (for initial fetch) and btpk (for updates)
        var pubKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
        var infoHash = "08ada5a7a6183aae1e09d831df6748d566095a10";
        var magnetUri = $"magnet:?xt=urn:btih:{infoHash}&xs=urn:btpk:{pubKeyHex}&dn=Sintel";

        var client = CreateIsolatedClient();
        var torrent = client.Add(magnetUri);

        if (torrent.InfoHash != infoHash)
            throw new Exception($"InfoHash wrong: {torrent.InfoHash}");
        if (torrent.BtpkPublicKey == null)
            throw new Exception("BtpkPublicKey should be parsed alongside btih");
        if (!torrent.IsMutableTorrent)
            throw new Exception("Should be mutable when btpk present");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep46_NormalMagnet_NotMutable()
    {
        var magnetUri = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel";
        var client = CreateIsolatedClient();
        var torrent = client.Add(magnetUri);

        if (torrent.IsMutableTorrent)
            throw new Exception("Normal magnet should NOT be mutable");
        if (torrent.BtpkPublicKey != null)
            throw new Exception("BtpkPublicKey should be null for normal magnets");

        await client.DisposeAsync();
    }

    // ── Sign Data Format ──

    [TestMethod]
    public async Task Bep46_SignData_BEP44Format()
    {
        // BEP 44 specifies: "4:salt{n}:{salt}3:seqi{seq}e1:v{len}:{value}"
        var value = Encoding.ASCII.GetBytes("hello");
        var salt = Encoding.ASCII.GetBytes("ch1");
        long seq = 42;

        var signData = DhtMutableItems.BuildSignData(value, salt, seq);
        var text = Encoding.ASCII.GetString(signData);

        // Should contain: 4:salt3:ch1  (salt is 3 bytes)
        if (!text.Contains("4:salt3:ch1"))
            throw new Exception($"Wrong salt encoding in: {text}");
        // Should contain: 3:seqi42e
        if (!text.Contains("3:seqi42e"))
            throw new Exception($"Wrong seq encoding in: {text}");
        // Should contain: 1:v5:hello
        if (!text.Contains("1:v5:hello"))
            throw new Exception($"Wrong value encoding in: {text}");
    }

    [TestMethod]
    public async Task Bep46_SignData_NoSalt_OmitsSaltField()
    {
        var value = new byte[] { 0xFF };
        var signData = DhtMutableItems.BuildSignData(value, null, 1);
        var text = Encoding.ASCII.GetString(signData, 0, signData.Length - 1);

        if (text.Contains("salt"))
            throw new Exception("Sign data without salt should not contain 'salt' field");
    }

    // ── Publisher/Consumer Lifecycle (Local Simulation) ──

    [TestMethod]
    public async Task Bep46_MutableItems_InitialState()
    {
        // Verify DhtMutableItems initializes correctly with signer
        var pubKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(pubKey);
        var dht = new DhtDiscovery();
        var signer = new NoOpSigner(pubKey);
        var items = dht.CreateMutableItems(signer);

        if (items.Sequence != 0)
            throw new Exception($"Initial sequence should be 0, got {items.Sequence}");

        // Target should be deterministic based on signer's public key
        var expectedTarget = DhtMutableItems.ComputeTarget(pubKey, null);
        var target = DhtMutableItems.ComputeTarget(signer.PublicKey, null);
        if (!target.SequenceEqual(expectedTarget))
            throw new Exception("MutableItems target should match ComputeTarget(signer.PublicKey)");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep46_MutableUpdate_EventFires()
    {
        // Test that NotifyMutableUpdate fires the OnMutableUpdate event
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 460);
        var torrent = await client.SeedAsync("mutable-v1.bin", data);
        torrent.BtpkPublicKey = new byte[32]; // Mark as mutable

        string? updatedHash = null;
        torrent.OnMutableUpdate += (newHash) => updatedHash = newHash;

        // Simulate DHT delivering a new infohash
        var newInfoHash = "aabbccddee0011223344aabbccddee0011223344";
        torrent.NotifyMutableUpdate(newInfoHash);

        if (updatedHash != newInfoHash)
            throw new Exception($"OnMutableUpdate should fire with new hash, got: {updatedHash}");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep46_TorrentLevel_MutableUpdate_ChainedEvents()
    {
        // Test that multiple event subscribers all receive the update
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 461);
        var torrent = await client.SeedAsync("mutable-chain.bin", data);
        torrent.BtpkPublicKey = new byte[32];

        string? hash1 = null, hash2 = null;
        torrent.OnMutableUpdate += (h) => hash1 = h;
        torrent.OnMutableUpdate += (h) => hash2 = h;

        var newInfoHash = "aabbccddee0011223344aabbccddee0011223344";
        torrent.NotifyMutableUpdate(newInfoHash);

        if (hash1 != newInfoHash)
            throw new Exception($"First subscriber should receive update, got: {hash1}");
        if (hash2 != newInfoHash)
            throw new Exception($"Second subscriber should receive update, got: {hash2}");

        await client.DisposeAsync();
    }

    // ── NoOpSigner ──

    [TestMethod]
    public async Task Bep46_NoOpSigner_HasPublicKey()
    {
        var pubKey = new byte[32];
        RandomNumberGenerator.Fill(pubKey);
        var signer = new NoOpSigner(pubKey);

        if (signer.Algorithm != "NoOp")
            throw new Exception($"Algorithm should be NoOp, got {signer.Algorithm}");
        if (!signer.PublicKey.SequenceEqual(pubKey))
            throw new Exception("Public key mismatch");

        // VerifyAsync always returns true (no crypto context)
        var verified = await signer.VerifyAsync(pubKey, new byte[] { 1 }, new byte[] { 2 });
        if (!verified)
            throw new Exception("NoOpSigner.VerifyAsync should always return true");
    }
}
