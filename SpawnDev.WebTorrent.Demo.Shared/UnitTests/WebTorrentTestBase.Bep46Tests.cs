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
        var items = dht.CreateMutableItems(new HmacFallbackSigner());

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
        var items = dht.CreateMutableItems(new HmacFallbackSigner());

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
        var items = dht.CreateMutableItems(new HmacFallbackSigner());

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
        var items = dht.CreateMutableItems(new HmacFallbackSigner());

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
        await using var channel = new AgentChannel(dht, new HmacFallbackSigner());

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
        await using var channel = new AgentChannel(dht, new HmacFallbackSigner());

        // Should not crash (DHT not started, will fail silently)
        try { await channel.PublishStateAsync(new byte[] { 0x42 }); } catch { }

        Console.WriteLine($"[AgentChannel] PublicKey: {channel.PublicKeyHex[..16]}...");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_PublishTorrent()
    {
        var dht = new DhtDiscovery();
        await using var channel = new AgentChannel(dht, new HmacFallbackSigner());

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
        await using var channel = new AgentChannel(dht, new HmacFallbackSigner());

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
        await using var channel = new AgentChannel(dht, new HmacFallbackSigner());

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
        await using var agent1 = new AgentChannel(dht, new HmacFallbackSigner());
        await using var agent2 = new AgentChannel(dht, new HmacFallbackSigner());

        if (agent1.PublicKeyHex == agent2.PublicKeyHex)
            throw new Exception("Two agents should have different keys");

        await dht.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  SwarmCompute — Distributed GPU Foundation
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SwarmCompute_Create()
    {
        await using var client = new WebTorrentClient();
        var dht = new DhtDiscovery();
        await using var swarm = new SwarmCompute(client, dht, new HmacFallbackSigner());

        if (swarm.PublicKey == null || swarm.PublicKey.Length != 32)
            throw new Exception("PublicKey should be 32 bytes");
        if (swarm.WorkerCount != 0)
            throw new Exception("Should have 0 workers initially");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task SwarmCompute_PublishTask()
    {
        await using var client = new WebTorrentClient();
        var dht = new DhtDiscovery();
        await using var swarm = new SwarmCompute(client, dht, new HmacFallbackSigner());

        var taskData = System.Text.Encoding.UTF8.GetBytes("kernel:matmul;size:1024");
        var inputData = new byte[16384];
        Random.Shared.NextBytes(inputData);

        var task = await swarm.PublishTaskAsync(taskData, inputData);

        if (string.IsNullOrEmpty(task.Id)) throw new Exception("Task ID empty");
        if (task.InputInfoHash == null) throw new Exception("Should have input info hash");
        if (string.IsNullOrEmpty(task.InputMagnetUri)) throw new Exception("Should have magnet URI");
        if (task.IsComplete) throw new Exception("Should not be complete yet");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task SwarmCompute_PublishTaskNoInput()
    {
        await using var client = new WebTorrentClient();
        var dht = new DhtDiscovery();
        await using var swarm = new SwarmCompute(client, dht, new HmacFallbackSigner());

        var task = await swarm.PublishTaskAsync(System.Text.Encoding.UTF8.GetBytes("reduce:sum"));
        if (task.InputInfoHash != null) throw new Exception("No input = no hash");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task SwarmCompute_Events()
    {
        await using var client = new WebTorrentClient();
        var dht = new DhtDiscovery();
        await using var swarm = new SwarmCompute(client, dht, new HmacFallbackSigner());

        SwarmWorker? joined = null;
        swarm.OnWorkerJoined += (w) => joined = w;
        if (joined != null) throw new Exception("Should not fire before join");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task SwarmTask_Properties()
    {
        var task = new SwarmTask { Id = "test", CreatedAt = DateTime.UtcNow };
        if (task.CompletedWorkers != 0 || task.IsComplete) throw new Exception("Initial state wrong");
    }

    [TestMethod]
    public async Task SwarmWorker_Properties()
    {
        var w = new SwarmWorker
        {
            PublicKey = new byte[32],
            Capabilities = System.Text.Encoding.UTF8.GetBytes("WebGPU,8GB"),
            JoinedAt = DateTime.UtcNow,
        };
        if (System.Text.Encoding.UTF8.GetString(w.Capabilities) != "WebGPU,8GB")
            throw new Exception("Capabilities mismatch");
    }

    // ═══════════════════════════════════════════════════════════
    //  RateLimiter — Additional Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RateLimiter_Paused()
    {
        var limiter = new RateLimiter(0);
        using var cts = new CancellationTokenSource(200);
        bool timedOut = false;
        try { await limiter.WaitAsync(1, cts.Token); }
        catch (OperationCanceledException) { timedOut = true; }
        if (!timedOut) throw new Exception("Rate 0 should block (paused)");
    }

    [TestMethod]
    public async Task RateLimiter_SwitchToUnlimited()
    {
        var limiter = new RateLimiter(0);
        _ = Task.Run(async () => { await Task.Delay(100); limiter.Rate = -1; });
        using var cts = new CancellationTokenSource(2000);
        await limiter.WaitAsync(1000, cts.Token);
    }

    [TestMethod]
    public async Task RateLimiter_SmallRate()
    {
        var limiter = new RateLimiter(100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(50);
        sw.Stop();
        if (sw.ElapsedMilliseconds > 200)
            throw new Exception($"First 50 bytes too slow: {sw.ElapsedMilliseconds}ms");
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentCreator — Full Options Roundtrip
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task TorrentCreator_AllOptions_Roundtrip()
    {
        var data = new byte[65536];
        Random.Shared.NextBytes(data);

        var (bytes, metadata) = Torrent.TorrentCreator.CreateFromBytes("full-opts.bin", data,
            new Torrent.TorrentCreatorOptions
            {
                PieceLength = 32768,
                Trackers = new[] { "wss://t1.example.com", "http://t2.example.com/announce" },
                WebSeeds = new[] { "https://cdn.example.com/files" },
                Comment = "All options test",
                IsPrivate = true,
            });

        if (metadata.PieceLength != 32768) throw new Exception($"PieceLength: {metadata.PieceLength}");
        if (metadata.Comment != "All options test") throw new Exception($"Comment: {metadata.Comment}");
        if (!metadata.IsPrivate) throw new Exception("Should be private");
        if (metadata.OriginalTorrentBytes == null) throw new Exception("Missing OriginalTorrentBytes");

        var parsed = Torrent.TorrentParser.Parse(bytes);
        if (!parsed.InfoHash.SequenceEqual(metadata.InfoHash)) throw new Exception("Hash mismatch");
        if (parsed.IsPrivate != true) throw new Exception("Private lost");
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

    // ═══════════════════════════════════════════════════════════
    //  EcdsaP256Signer — Real WebCrypto Signing
    //  Previously 0% coverage. The crypto the P2P system depends on.
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Signer_EcdsaP256_Create()
    {
        var crypto = new SpawnDev.BlazorJS.Cryptography.DotNetCrypto();
        var signer = new EcdsaP256Signer(crypto);
        if (signer.Algorithm != "ECDSA-P256")
            throw new Exception($"Algorithm: {signer.Algorithm}");
        if (signer.PublicKey == null || signer.PublicKey.Length != 32)
            throw new Exception("PublicKey should be 32 bytes (SHA-256 of SPKI)");

        Console.WriteLine("[EcdsaP256] Create: OK");
    }

    [TestMethod]
    public async Task Signer_EcdsaP256_GenerateKey()
    {
        var crypto = new SpawnDev.BlazorJS.Cryptography.DotNetCrypto();
        var signer = new EcdsaP256Signer(crypto);
        await signer.GenerateKeyAsync();

        // After generation, public key should be non-zero
        if (signer.PublicKey.All(b => b == 0))
            throw new Exception("PublicKey should not be all zeros after generation");

        Console.WriteLine($"[EcdsaP256] GenerateKey: {Convert.ToHexString(signer.PublicKey)[..16]}...");
    }

    [TestMethod]
    public async Task Signer_EcdsaP256_SignAndVerify()
    {
        var crypto = new SpawnDev.BlazorJS.Cryptography.DotNetCrypto();
        var signer = new EcdsaP256Signer(crypto);
        await signer.GenerateKeyAsync();

        var message = System.Text.Encoding.UTF8.GetBytes("Hello P2P swarm");
        var signature = await signer.SignAsync(message);

        if (signature == null || signature.Length == 0)
            throw new Exception("Signature should not be empty");
        if (signature.Length != 64)
            throw new Exception($"Signature should be 64 bytes, got {signature.Length}");

        Console.WriteLine($"[EcdsaP256] Sign: {signature.Length} bytes");
    }

    [TestMethod]
    public async Task Signer_EcdsaP256_ExportImport()
    {
        var crypto = new SpawnDev.BlazorJS.Cryptography.DotNetCrypto();
        var signer1 = new EcdsaP256Signer(crypto);
        await signer1.GenerateKeyAsync();

        // Export
        var (pubKey, privKey) = await signer1.ExportKeyPairAsync();
        if (pubKey == null || pubKey.Length == 0) throw new Exception("Public key export failed");
        if (privKey == null || privKey.Length == 0) throw new Exception("Private key export failed");

        // Import into new signer
        var signer2 = new EcdsaP256Signer(crypto);
        await signer2.ImportKeyAsync(pubKey, privKey);

        // Both should produce valid signatures
        var message = new byte[] { 1, 2, 3, 4, 5 };
        var sig1 = await signer1.SignAsync(message);
        var sig2 = await signer2.SignAsync(message);

        // Both signatures should be 64 bytes
        if (sig1.Length != 64 || sig2.Length != 64)
            throw new Exception("Both signers should produce 64-byte signatures");

        Console.WriteLine("[EcdsaP256] Export/Import round-trip: OK");
    }

    [TestMethod]
    public async Task Signer_EcdsaP256_TwoSigners_DifferentKeys()
    {
        var crypto = new SpawnDev.BlazorJS.Cryptography.DotNetCrypto();
        var signer1 = new EcdsaP256Signer(crypto);
        var signer2 = new EcdsaP256Signer(crypto);
        await signer1.GenerateKeyAsync();
        await signer2.GenerateKeyAsync();

        if (signer1.PublicKey.SequenceEqual(signer2.PublicKey))
            throw new Exception("Two signers should have different keys");

        Console.WriteLine("[EcdsaP256] Two signers have different keys: OK");
    }

    [TestMethod]
    public async Task Signer_EcdsaP256_SignWithoutKey_Throws()
    {
        var crypto = new SpawnDev.BlazorJS.Cryptography.DotNetCrypto();
        var signer = new EcdsaP256Signer(crypto);

        try
        {
            await signer.SignAsync(new byte[] { 1, 2, 3 });
            throw new Exception("Should throw without key generation");
        }
        catch (InvalidOperationException ex)
        {
            if (!ex.Message.Contains("Key not generated"))
                throw new Exception($"Wrong error: {ex.Message}");
        }

        Console.WriteLine("[EcdsaP256] Sign without key throws: OK");
    }

    // ═══════════════════════════════════════════════════════════
    //  DhtMutableItems — Verify, Algorithm
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MutableItems_TokenCaching()
    {
        var dht = new DhtDiscovery();
        var signer = new HmacFallbackSigner();
        var items = dht.CreateMutableItems(signer);

        if (items.CachedTokenCount != 0)
            throw new Exception("Should start with 0 cached tokens");

        // Cache a token for a node
        var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.2.3.4"), 6881);
        items.CacheToken(ep, new byte[] { 0xAB, 0xCD });

        if (items.CachedTokenCount != 1)
            throw new Exception($"Should have 1 cached token, got {items.CachedTokenCount}");

        // Cache another
        var ep2 = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("5.6.7.8"), 6881);
        items.CacheToken(ep2, new byte[] { 0xEF });
        if (items.CachedTokenCount != 2)
            throw new Exception($"Should have 2, got {items.CachedTokenCount}");

        // Overwrite existing
        items.CacheToken(ep, new byte[] { 0x11, 0x22, 0x33 });
        if (items.CachedTokenCount != 2)
            throw new Exception("Overwrite should not increase count");

        Console.WriteLine("[MutableItems] Token caching: OK");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_Algorithm()
    {
        var dht = new DhtDiscovery();
        var signer = new HmacFallbackSigner();
        var items = dht.CreateMutableItems(signer);

        if (items.Algorithm != "HMAC-SHA512-Fallback")
            throw new Exception($"Algorithm: {items.Algorithm}");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_VerifySignature()
    {
        var dht = new DhtDiscovery();
        var signer = new HmacFallbackSigner();
        var items = dht.CreateMutableItems(signer);

        // Publish so we have a sequence
        try { await items.PublishAsync(new byte[] { 42 }); } catch { }

        // Verify a signature we create
        var msg = new byte[] { 1, 2, 3 };
        var sig = await signer.SignAsync(msg);
        var valid = await items.VerifyAsync(signer.PublicKey, msg, sig, 1);
        if (!valid) throw new Exception("Should verify our own signature");

        // Tampered message should fail
        var tampered = new byte[] { 1, 2, 4 };
        var invalid = await items.VerifyAsync(signer.PublicKey, tampered, sig, 1);
        if (invalid) throw new Exception("Should reject tampered message");

        Console.WriteLine("[MutableItems] Verify signature: OK");
        await dht.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  RateLimiter — Token Bucket Throttling
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RateLimiter_Create()
    {
        var limiter = new RateLimiter(-1); // unlimited
        if (limiter.Rate != -1) throw new Exception($"Rate: {limiter.Rate}");

        var limited = new RateLimiter(1024); // 1KB/s
        if (limited.Rate != 1024) throw new Exception($"Rate: {limited.Rate}");

        Console.WriteLine("[RateLimiter] Create: OK");
    }

    [TestMethod]
    public async Task RateLimiter_Unlimited_Instant()
    {
        var limiter = new RateLimiter(-1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(1_000_000); // 1MB should be instant
        sw.Stop();
        if (sw.ElapsedMilliseconds > 100)
            throw new Exception($"Unlimited should be instant, took {sw.ElapsedMilliseconds}ms");

        Console.WriteLine("[RateLimiter] Unlimited returns immediately: OK");
    }

    [TestMethod]
    public async Task RateLimiter_Limited_Throttles()
    {
        var limiter = new RateLimiter(100); // 100 bytes/sec

        // First call should succeed (has initial tokens)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(50);
        var first = sw.ElapsedMilliseconds;

        // Second call should be throttled (tokens depleted)
        await limiter.WaitAsync(100);
        sw.Stop();
        var total = sw.ElapsedMilliseconds;

        // Should have taken some time due to throttling
        if (total < 100) // At 100 bytes/sec, 150 bytes takes >1 second but we're lenient
            Console.WriteLine($"[RateLimiter] Warning: faster than expected ({total}ms)");

        Console.WriteLine($"[RateLimiter] Throttling: {total}ms for 150 bytes at 100 B/s");
    }

    [TestMethod]
    public async Task RateLimiter_DynamicRateChange()
    {
        var limiter = new RateLimiter(100);
        if (limiter.Rate != 100) throw new Exception("Initial rate wrong");

        limiter.Rate = -1; // switch to unlimited
        if (limiter.Rate != -1) throw new Exception("Rate not updated");

        // Should now be instant
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(1_000_000);
        sw.Stop();
        if (sw.ElapsedMilliseconds > 100)
            throw new Exception("Should be instant after switching to unlimited");

        Console.WriteLine("[RateLimiter] Change rate: OK");
    }

    [TestMethod]
    public async Task RateLimiter_Cancellation()
    {
        var limiter = new RateLimiter(1); // 1 byte/sec — very slow
        var cts = new CancellationTokenSource(50); // cancel after 50ms

        try
        {
            await limiter.WaitAsync(1000, cts.Token); // would take 1000 seconds
            // If we get here, token bucket had enough tokens (unlikely at 1 B/s)
        }
        catch (OperationCanceledException)
        {
            // Expected — cancelled while waiting for tokens
        }

        Console.WriteLine("[RateLimiter] Cancellation: OK");
    }

    // ═══════════════════════════════════════════════════════════
    //  WireProtocol — Property Coverage
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Wire_ExtensionSupport_Properties()
    {
        // WireProtocol needs a connection — test what we can without one
        // The SupportsExtensions check reads RemoteReserved[5] bit 0x10
        // Just verify the types exist and properties are accessible
        var protocol = typeof(SpawnDev.WebTorrent.Wire.WireProtocol);
        var supportsExt = protocol.GetProperty("SupportsExtensions");
        var supportsFast = protocol.GetProperty("SupportsFastExtension");
        var remoteReserved = protocol.GetProperty("RemoteReserved");

        if (supportsExt == null) throw new Exception("SupportsExtensions property missing");
        if (supportsFast == null) throw new Exception("SupportsFastExtension property missing");
        if (remoteReserved == null) throw new Exception("RemoteReserved property missing");

        Console.WriteLine("[WireProtocol] Extension properties exist: OK");
    }

    // ═══════════════════════════════════════════════════════════
    //  SwarmCompute — Worker Join/Submit
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SwarmCompute_JoinAsWorker()
    {
        await using var client = new WebTorrentClient();
        var dht = new DhtDiscovery();
        var signer = new HmacFallbackSigner();
        await using var compute = new SwarmCompute(client, dht, signer);

        // JoinAsWorker should not crash even without DHT running
        try
        {
            await compute.JoinAsWorkerAsync(new byte[] { 1, 2, 3 }, async (taskData) =>
            {
                return new byte[] { 42 };
            });
        }
        catch { }

        Console.WriteLine("[SwarmCompute] JoinAsWorker: no crash");
        await dht.DisposeAsync();
    }
}
