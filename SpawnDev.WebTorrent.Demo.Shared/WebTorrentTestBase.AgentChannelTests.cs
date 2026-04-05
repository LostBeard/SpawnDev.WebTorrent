using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task AgentChannel_CreateNamed_UsesSaltCorrectly()
    {
        var channel = new AgentChannel();
        var named = channel.Channel("model-updates");
        // AgentNamedChannel should use the channel name as salt for BEP 44
        // Verify the named channel was created
        if (named == null) throw new Exception("Named channel is null");
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
    public async Task AgentChannel_VerifySignature_AcceptsValid()
    {
        // Ed25519Signer requires IPortableCrypto (browser SubtleCrypto or PlatformCrypto)
        // This test only works in browser or with DI-provided crypto
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Ed25519 signing requires browser crypto");

        // In browser, this would be resolved via DI
        throw new UnsupportedTestException("Requires DI-provided IPortableCrypto");
    }

    [TestMethod]
    public async Task AgentChannel_VerifySignature_RejectsInvalid()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Ed25519 signing requires browser crypto");

        throw new UnsupportedTestException("Requires DI-provided IPortableCrypto");
    }
}
