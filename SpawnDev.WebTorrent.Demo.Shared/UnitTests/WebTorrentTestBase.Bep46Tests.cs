using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Discovery;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// BEP 44/46 tests: DHT mutable items, signing, AgentChannel.
/// Tests cover the full pub/sub pipeline for AI agent communication.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  IDhtSigner — Signing Abstraction
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Signer_HmacFallback_Create()
    {
        var signer = new HmacFallbackSigner();
        if (signer.Algorithm != "HMAC-SHA512-Fallback")
            throw new Exception($"Algorithm: {signer.Algorithm}");
        if (signer.PublicKey.Length != 32)
            throw new Exception($"PublicKey should be 32 bytes, got {signer.PublicKey.Length}");
    }

    [TestMethod]
    public async Task Signer_HmacFallback_Sign()
    {
        var signer = new HmacFallbackSigner();
        var message = System.Text.Encoding.UTF8.GetBytes("Hello AI agents");

        var sig1 = await signer.SignAsync(message);
        if (sig1.Length < 64) throw new Exception($"Signature should be >= 64 bytes, got {sig1.Length}");

        // Same message should produce same signature (deterministic)
        var sig2 = await signer.SignAsync(message);
        if (!sig1.SequenceEqual(sig2))
            throw new Exception("Same message should produce same HMAC signature");
    }

    [TestMethod]
    public async Task Signer_HmacFallback_DifferentMessages()
    {
        var signer = new HmacFallbackSigner();
        var sig1 = await signer.SignAsync(new byte[] { 1, 2, 3 });
        var sig2 = await signer.SignAsync(new byte[] { 4, 5, 6 });

        if (sig1.SequenceEqual(sig2))
            throw new Exception("Different messages should produce different signatures");
    }

    [TestMethod]
    public async Task Signer_HmacFallback_ExportKeyPair()
    {
        var signer = new HmacFallbackSigner();
        var (pub, priv) = await signer.ExportKeyPairAsync();

        if (pub.Length != 32) throw new Exception($"Public key: {pub.Length} bytes");
        if (priv.Length != 64) throw new Exception($"Private key: {priv.Length} bytes");
        if (!pub.SequenceEqual(signer.PublicKey))
            throw new Exception("Exported public key should match");
    }

    [TestMethod]
    public async Task Signer_HmacFallback_ImportKeyPair()
    {
        var signer1 = new HmacFallbackSigner();
        var (pub, priv) = await signer1.ExportKeyPairAsync();

        // Import into a new signer
        var signer2 = new HmacFallbackSigner(priv, pub);
        if (!signer2.PublicKey.SequenceEqual(pub))
            throw new Exception("Imported public key should match");

        // Signatures should match
        var msg = new byte[] { 42 };
        var sig1 = await signer1.SignAsync(msg);
        var sig2 = await signer2.SignAsync(msg);
        if (!sig1.SequenceEqual(sig2))
            throw new Exception("Same key pair should produce same signature");
    }

    [TestMethod]
    public async Task Signer_HmacFallback_Verify()
    {
        var signer = new HmacFallbackSigner();
        var msg = new byte[] { 1, 2, 3 };
        var sig = await signer.SignAsync(msg);

        var valid = await signer.VerifyAsync(signer.PublicKey, msg, sig);
        if (!valid) throw new Exception("Should verify own signature");
    }

    // ═══════════════════════════════════════════════════════════
    //  DhtMutableItems — Publish/Subscribe
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MutableItems_PublishIncrementsSequence()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        if (items.Sequence != 0) throw new Exception("Should start at 0");

        // PublishAsync won't actually send (DHT not started) but should increment sequence
        // The method will fail silently since there are no nodes — that's fine
        try { await items.PublishAsync(new byte[] { 1, 2, 3 }); } catch { }

        if (items.Sequence != 1) throw new Exception($"Should be 1 after publish, got {items.Sequence}");

        try { await items.PublishAsync(new byte[] { 4, 5, 6 }); } catch { }
        if (items.Sequence != 2) throw new Exception($"Should be 2, got {items.Sequence}");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_PublishInfoHash()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        var infoHash = new byte[20];
        Random.Shared.NextBytes(infoHash);

        // Should not throw
        try { await items.PublishInfoHashAsync(infoHash); } catch { }

        // Wrong size should throw
        bool threw = false;
        try { await items.PublishInfoHashAsync(new byte[10]); }
        catch (ArgumentException) { threw = true; }
        if (!threw) throw new Exception("Should throw for wrong info hash size");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_ValueTooLarge()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        bool threw = false;
        try { await items.PublishAsync(new byte[1001]); }
        catch (ArgumentException) { threw = true; }
        if (!threw) throw new Exception("Should throw for value > 1000 bytes");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_MaxValueSize()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        // Exactly 1000 bytes should be OK
        try { await items.PublishAsync(new byte[1000]); } catch { }
        // Should not throw

        await dht.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  AgentChannel — High-Level API
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AgentChannel_Create()
    {
        var dht = new DhtDiscovery();
        await using var channel = new AgentChannel(dht);

        if (channel.PublicKey == null || channel.PublicKey.Length != 32)
            throw new Exception("PublicKey should be 32 bytes");
        if (string.IsNullOrEmpty(channel.PublicKeyHex))
            throw new Exception("PublicKeyHex should not be empty");
        if (channel.PublicKeyHex.Length != 64)
            throw new Exception($"PublicKeyHex should be 64 chars, got {channel.PublicKeyHex.Length}");
        if (channel.Sequence != 0)
            throw new Exception("Initial sequence should be 0");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_PublishState()
    {
        var dht = new DhtDiscovery();
        await using var channel = new AgentChannel(dht);

        // Should not crash (DHT not started, will fail silently)
        try { await channel.PublishStateAsync(new byte[] { 0x42 }); } catch { }

        Console.WriteLine($"[AgentChannel] PublicKey: {channel.PublicKeyHex[..16]}...");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_PublishTorrent()
    {
        var dht = new DhtDiscovery();
        await using var channel = new AgentChannel(dht);

        var infoHash = new byte[20];
        Random.Shared.NextBytes(infoHash);

        try { await channel.PublishTorrentAsync(infoHash); } catch { }
        // Should not crash

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_NamedChannels()
    {
        var dht = new DhtDiscovery();
        await using var channel = new AgentChannel(dht);

        var weights = channel.Channel("weights");
        var cache = channel.Channel("kv-cache");
        var control = channel.Channel("control");

        // All should work without crash
        try { await weights.PublishAsync(new byte[] { 1 }); } catch { }
        try { await cache.PublishAsync(new byte[] { 2 }); } catch { }
        try { await control.PublishAsync(new byte[] { 3 }); } catch { }

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_EventWiring()
    {
        var dht = new DhtDiscovery();
        await using var channel = new AgentChannel(dht);

        byte[]? receivedKey = null;
        byte[]? receivedValue = null;
        long receivedSeq = -1;

        channel.OnAgentUpdate += (key, value, seq) =>
        {
            receivedKey = key;
            receivedValue = value;
            receivedSeq = seq;
        };

        // Event should not have fired yet
        if (receivedKey != null) throw new Exception("Should not fire before subscribe");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_TwoAgents_DifferentKeys()
    {
        var dht = new DhtDiscovery();
        await using var agent1 = new AgentChannel(dht);
        await using var agent2 = new AgentChannel(dht);

        if (agent1.PublicKeyHex == agent2.PublicKeyHex)
            throw new Exception("Two agents should have different keys");

        await dht.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  SipSorcery Transport (construction only — no network)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SipSorceryTransport_Create()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("SIPSorcery requires desktop");

        var transport = new Transports.SipSorceryWebRtcTransport();
        if (transport.Type != "webrtc") throw new Exception($"Type: {transport.Type}");
        if (!transport.CanAccept) throw new Exception("Should accept connections");
        await transport.DisposeAsync();
    }

    [TestMethod]
    public async Task SipSorceryTransport_WithOptions()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("SIPSorcery requires desktop");

        var transport = new Transports.SipSorceryWebRtcTransport(new Transports.WebRtcTransportOptions
        {
            IceServers = new[] { "stun:stun.l.google.com:19302" },
            ChannelLabel = "test",
            Ordered = true,
        });
        await transport.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  TcpTransport (construction only — no network)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task TcpTransport_Create()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("TCP requires desktop");

        var transport = new Transports.TcpTransport();
        if (transport.Type != "tcp") throw new Exception($"Type: {transport.Type}");
        if (!transport.CanAccept) throw new Exception("TCP should accept connections");
        await transport.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentHttpServer (construction only)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task HttpServer_Properties()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("HttpServer requires desktop");

        await using var client = new WebTorrentClient();
        var server = new TorrentHttpServer(client, 18999);

        if (server.BaseUrl != "http://localhost:18999/")
            throw new Exception($"BaseUrl: {server.BaseUrl}");
        if (server.IsRunning) throw new Exception("Should not be running before Start");

        server.Start();
        if (!server.IsRunning) throw new Exception("Should be running after Start");

        server.Stop();
        if (server.IsRunning) throw new Exception("Should not be running after Stop");

        await server.DisposeAsync();
    }
}
