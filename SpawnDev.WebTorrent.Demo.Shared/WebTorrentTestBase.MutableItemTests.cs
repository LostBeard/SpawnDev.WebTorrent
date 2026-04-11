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
    public async Task MutableItem_SignData_SequenceAndValueEncodedCorrectly()
    {
        // Verify sign data encoding handles various sequence values and value lengths
        var smallValue = new byte[] { 0x01 };
        var largeValue = new byte[200]; // near the BEP 44 1000-byte limit
        System.Security.Cryptography.RandomNumberGenerator.Fill(largeValue);

        // Sequence 0
        var sd0 = DhtMutableItems.BuildSignData(smallValue, null, 0);
        var str0 = System.Text.Encoding.ASCII.GetString(sd0, 0, sd0.Length - smallValue.Length);
        if (!str0.Contains("3:seqi0e")) throw new Exception($"Seq 0 not encoded correctly: {str0}");

        // Large sequence
        var sdMax = DhtMutableItems.BuildSignData(smallValue, null, 999999);
        var strMax = System.Text.Encoding.ASCII.GetString(sdMax, 0, sdMax.Length - smallValue.Length);
        if (!strMax.Contains("3:seqi999999e")) throw new Exception($"Large seq not encoded: {strMax}");

        // Large value - length prefix should match
        var sdLarge = DhtMutableItems.BuildSignData(largeValue, null, 1);
        var strLarge = System.Text.Encoding.ASCII.GetString(sdLarge, 0, sdLarge.Length - largeValue.Length);
        if (!strLarge.Contains($"1:v{largeValue.Length}:"))
            throw new Exception($"Large value length not encoded correctly: {strLarge}");
    }
}
