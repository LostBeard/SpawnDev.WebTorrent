using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task AgentChannel_CreateNamed_ProducesDistinctChannels()
    {
        var channel = new AgentChannel();
        var named1 = channel.Channel("model-updates");
        var named2 = channel.Channel("compute-tasks");

        if (named1 == null) throw new Exception("Named channel 1 is null");
        if (named2 == null) throw new Exception("Named channel 2 is null");

        // Different channel names should produce different instances
        if (ReferenceEquals(named1, named2))
            throw new Exception("Different channel names should produce different instances");

        // Each call creates a fresh channel scoped to that name
        var named3 = channel.Channel("model-updates");
        if (named3 == null) throw new Exception("Named channel 3 is null");

        await channel.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_RelayMessage_SerializesCorrectly()
    {
        var msg = new AgentRelayMessage
        {
            PublicKey = "abcdef1234567890",
            Sequence = 42,
            Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            Salt = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test")),
            Signature = Convert.ToBase64String(new byte[] { 0xAA, 0xBB }),
        };

        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<AgentRelayMessage>(json);

        if (deserialized == null) throw new Exception("Deserialization failed");
        if (deserialized.PublicKey != msg.PublicKey) throw new Exception("PublicKey mismatch");
        if (deserialized.Sequence != 42) throw new Exception($"Sequence wrong: {deserialized.Sequence}");
        if (deserialized.Data != msg.Data) throw new Exception("Data mismatch");
        if (deserialized.Salt != msg.Salt) throw new Exception("Salt mismatch");
        if (deserialized.Signature != msg.Signature) throw new Exception("Signature mismatch");
    }

    [TestMethod]
    public async Task AgentChannel_NoOpSigner_VerifyAlwaysTrue()
    {
        // NoOpSigner.VerifyAsync always returns true - useful for test/resolution-only scenarios
        var pubKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(pubKey);
        var signer = new NoOpSigner(pubKey);

        if (signer.Algorithm != "NoOp") throw new Exception($"Algorithm should be NoOp, got {signer.Algorithm}");
        if (!signer.PublicKey.SequenceEqual(pubKey)) throw new Exception("PublicKey mismatch");

        // Verify should accept any signature
        var verified = await signer.VerifyAsync(pubKey, new byte[] { 1, 2, 3 }, new byte[] { 0xAA, 0xBB });
        if (!verified) throw new Exception("NoOpSigner.VerifyAsync should always return true");

        // Verify with different data should still return true
        var verified2 = await signer.VerifyAsync(new byte[32], new byte[0], new byte[0]);
        if (!verified2) throw new Exception("NoOpSigner.VerifyAsync should return true for any input");

        // SignAsync should throw NotSupportedException (NoOp can verify but not sign)
        bool threw = false;
        try { await signer.SignAsync(new byte[] { 1 }); }
        catch (NotSupportedException) { threw = true; }
        if (!threw) throw new Exception("NoOpSigner.SignAsync should throw NotSupportedException");
    }

    [TestMethod]
    public async Task AgentChannel_RelayMessage_AllFieldsPreserved()
    {
        // Test that all fields survive JSON round-trip, including edge cases
        var msg = new AgentRelayMessage
        {
            PublicKey = new string('a', 64), // 32 bytes as hex
            Sequence = long.MaxValue,
            Data = Convert.ToBase64String(new byte[1000]), // large payload
            Salt = "", // empty salt
            Signature = Convert.ToBase64String(new byte[] { 0 }),
        };

        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<AgentRelayMessage>(json);

        if (deserialized == null) throw new Exception("Deserialization failed");
        if (deserialized.Sequence != long.MaxValue) throw new Exception("Max sequence not preserved");
        if (deserialized.Salt != "") throw new Exception("Empty salt not preserved");
        if (deserialized.Data != msg.Data) throw new Exception("Large data not preserved");
    }
}
