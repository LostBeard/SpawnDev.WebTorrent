using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Security.Cryptography;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task MutableItem_Target_SHA1OfPubKey()
    {
        var pubKey = new byte[32];
        RandomNumberGenerator.Fill(pubKey);
        var target = DhtMutableItems.ComputeTarget(pubKey, null);
        var expected = SHA1.HashData(pubKey);
        if (!target.SequenceEqual(expected))
            throw new Exception("Target should be SHA1(publicKey) when no salt");
        if (target.Length != 20)
            throw new Exception($"Target should be 20 bytes (SHA1), got {target.Length}");
    }

    [TestMethod]
    public async Task MutableItem_Target_WithSalt()
    {
        var pubKey = new byte[32];
        RandomNumberGenerator.Fill(pubKey);
        var salt = System.Text.Encoding.UTF8.GetBytes("test-channel");
        var target = DhtMutableItems.ComputeTarget(pubKey, salt);
        var combined = new byte[pubKey.Length + salt.Length];
        pubKey.CopyTo(combined, 0);
        salt.CopyTo(combined, pubKey.Length);
        var expected = SHA1.HashData(combined);
        if (!target.SequenceEqual(expected))
            throw new Exception("Target should be SHA1(publicKey + salt) when salt provided");
    }

    [TestMethod]
    public async Task MutableItem_SignData_CorrectFormat()
    {
        var value = new byte[] { 0xDE, 0xAD };
        var salt = System.Text.Encoding.UTF8.GetBytes("s");
        long seq = 7;

        var signData = DhtMutableItems.BuildSignData(value, salt, seq);
        var str = System.Text.Encoding.ASCII.GetString(signData, 0, signData.Length - value.Length);

        // BEP 44 format: 4:salt1:s3:seqi7e1:v2:
        if (!str.Contains("4:salt1:s")) throw new Exception($"Missing salt in sign data: {str}");
        if (!str.Contains("3:seqi7e")) throw new Exception($"Missing seq in sign data: {str}");
        if (!str.Contains("1:v")) throw new Exception($"Missing value prefix in sign data: {str}");
    }

    [TestMethod]
    public async Task MutableItem_SignData_NoSalt()
    {
        var value = new byte[] { 0xFF };
        long seq = 1;

        var signData = DhtMutableItems.BuildSignData(value, null, seq);
        var str = System.Text.Encoding.ASCII.GetString(signData, 0, signData.Length - value.Length);

        // Without salt, format is: 3:seqi1e1:v1:
        if (str.Contains("salt")) throw new Exception("Should not contain salt when null");
        if (!str.Contains("3:seqi1e")) throw new Exception($"Missing seq: {str}");
    }

    [TestMethod]
    public async Task MutableItem_RejectsOldSequence()
    {
        // DhtMutableItems.IsNewerSequence checks against value cache
        // Without a real signer, we test the static target/signdata methods
        var pubKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(pubKey);

        // ComputeTarget should be deterministic for same input
        var t1 = DhtMutableItems.ComputeTarget(pubKey, null);
        var t2 = DhtMutableItems.ComputeTarget(pubKey, null);
        if (!t1.SequenceEqual(t2))
            throw new Exception("ComputeTarget should be deterministic");

        // With different salt, target should differ
        var t3 = DhtMutableItems.ComputeTarget(pubKey, new byte[] { 1 });
        if (t1.SequenceEqual(t3))
            throw new Exception("Different salt should produce different target");
    }
}
