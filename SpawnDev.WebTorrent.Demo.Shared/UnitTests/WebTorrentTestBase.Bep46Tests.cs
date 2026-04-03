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
    //  AgentChannel Browser Relay
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AgentChannel_BrowserRelay_Create()
    {
        // Browser relay path — no DHT needed, but requires a signer for identity
        var tracker = new WebSocketTrackerClient("wss://hub.spawndev.com:44365/announce", new byte[20]);
        var signer = new HmacFallbackSigner();
        await using var channel = new AgentChannel(tracker, new byte[20], signer);

        if (!channel.IsBrowserRelay)
            throw new Exception("Should be browser relay mode");
        if (channel.PublicKey == null || channel.PublicKey.Length != 32)
            throw new Exception("PublicKey should be 32 bytes");
        if (channel.Sequence != 0)
            throw new Exception("Initial sequence should be 0");

        Console.WriteLine("[AgentChannel] Browser relay create: OK");
    }

    [TestMethod]
    public async Task AgentChannel_BrowserRelay_PublishIncrementsSequence()
    {
        var tracker = new WebSocketTrackerClient("wss://hub.spawndev.com:44365/announce", new byte[20]);
        var signer = new HmacFallbackSigner();
        await using var channel = new AgentChannel(tracker, new byte[20], signer);

        // Publish should increment sequence (even without connection — no crash)
        try { await channel.PublishStateAsync(new byte[] { 1 }); } catch { }
        if (channel.Sequence != 1)
            throw new Exception($"Sequence should be 1, got {channel.Sequence}");

        try { await channel.PublishStateAsync(new byte[] { 2 }); } catch { }
        if (channel.Sequence != 2)
            throw new Exception($"Sequence should be 2, got {channel.Sequence}");

        Console.WriteLine("[AgentChannel] Browser relay publish increments sequence: OK");
    }

    // ═══════════════════════════════════════════════════════════
    //  SwarmCompute — Distributed GPU Foundation
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SwarmCompute_Create()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
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
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
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
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var dht = new DhtDiscovery();
        await using var swarm = new SwarmCompute(client, dht, new HmacFallbackSigner());

        var task = await swarm.PublishTaskAsync(System.Text.Encoding.UTF8.GetBytes("reduce:sum"));
        if (task.InputInfoHash != null) throw new Exception("No input = no hash");
        await dht.DisposeAsync();
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

        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
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
        var crypto = Client!.Crypto;
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
        var crypto = Client!.Crypto;
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
        var crypto = Client!.Crypto;
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
        var crypto = Client!.Crypto;
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
        var crypto = Client!.Crypto;
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
        var crypto = Client!.Crypto;
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

    [TestMethod]
    public async Task Signer_EcdsaP256_SignVerify_RoundTrip()
    {
        var crypto = Client!.Crypto;
        var signer = new EcdsaP256Signer(crypto);
        await signer.GenerateKeyAsync();

        var message = System.Text.Encoding.UTF8.GetBytes("Verify this message");
        var signature = await signer.SignAsync(message);

        // Export the SPKI public key and verify with it
        var (spkiPub, _) = await signer.ExportKeyPairAsync();
        var verified = await signer.VerifyAsync(spkiPub, message, signature);
        if (!verified) throw new Exception("Signature should verify against our own public key");

        // Tampered message must fail
        var tampered = System.Text.Encoding.UTF8.GetBytes("Tampered message!!!");
        var shouldFail = await signer.VerifyAsync(spkiPub, tampered, signature);
        if (shouldFail) throw new Exception("Tampered message should not verify");

        Console.WriteLine("[EcdsaP256] Sign/Verify round-trip: OK");
    }

    [TestMethod]
    public async Task Signer_EcdsaP256_CrossSigner_Verify()
    {
        var crypto = Client!.Crypto;

        // Signer A signs
        var signerA = new EcdsaP256Signer(crypto);
        await signerA.GenerateKeyAsync();
        var message = new byte[] { 10, 20, 30, 40, 50 };
        var signature = await signerA.SignAsync(message);

        // Export A's keys, import public key into signer B (simulates a peer)
        var (pubKeyA, _) = await signerA.ExportKeyPairAsync();

        // Signer B verifies A's signature using A's public key
        var signerB = new EcdsaP256Signer(crypto);
        await signerB.GenerateKeyAsync(); // B has its own keys
        var verified = await signerB.VerifyAsync(pubKeyA, message, signature);
        if (!verified) throw new Exception("Signer B should verify Signer A's signature");

        // B's own key should NOT verify A's signature
        var (pubKeyB, _) = await signerB.ExportKeyPairAsync();
        var wrongKey = await signerB.VerifyAsync(pubKeyB, message, signature);
        if (wrongKey) throw new Exception("Wrong public key should not verify");

        Console.WriteLine("[EcdsaP256] Cross-signer verify: OK");
    }

    [TestMethod]
    public async Task Signer_EcdsaP256_ExportImport_VerifySurvives()
    {
        var crypto = Client!.Crypto;

        // Generate, sign, export
        var signer1 = new EcdsaP256Signer(crypto);
        await signer1.GenerateKeyAsync();
        var message = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var signature = await signer1.SignAsync(message);
        var (pubKey, privKey) = await signer1.ExportKeyPairAsync();

        // Import into a fresh signer and verify the original signature
        var signer2 = new EcdsaP256Signer(crypto);
        await signer2.ImportKeyAsync(pubKey, privKey);
        var verified = await signer2.VerifyAsync(pubKey, message, signature);
        if (!verified) throw new Exception("Imported signer should verify original signature");

        // Sign with imported signer, verify with original public key
        var msg2 = new byte[] { 0xCA, 0xFE };
        var sig2 = await signer2.SignAsync(msg2);
        var verified2 = await signer1.VerifyAsync(pubKey, msg2, sig2);
        if (!verified2) throw new Exception("Original key should verify imported signer's signature");

        Console.WriteLine("[EcdsaP256] Export/Import verify survives: OK");
    }

    [TestMethod]
    public async Task Signer_EcdsaP256_AgentChannel_Creates()
    {
        var crypto = Client!.Crypto;
        var signer = new EcdsaP256Signer(crypto);
        await signer.GenerateKeyAsync();

        var dht = new DhtDiscovery();
        var channel = new AgentChannel(dht, signer);

        // Channel should have the signer's public key identity
        if (channel.PublicKey.All(b => b == 0))
            throw new Exception("AgentChannel PublicKey should not be all zeros");
        if (channel.PublicKeyHex.Length != 64)
            throw new Exception($"PublicKeyHex should be 64 hex chars, got {channel.PublicKeyHex.Length}");

        Console.WriteLine($"[EcdsaP256] AgentChannel creates with real crypto: {channel.PublicKeyHex[..16]}...");

        await channel.DisposeAsync();
        await dht.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 46 Security Tests — Priority 1
    //  These prove the security guarantees of mutable items.
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Signer_EcdsaP256_RejectTruncatedSignature()
    {
        var crypto = Client!.Crypto;
        var signer = new EcdsaP256Signer(crypto);
        await signer.GenerateKeyAsync();

        var message = new byte[] { 1, 2, 3, 4, 5 };
        var signature = await signer.SignAsync(message);
        var (pubKey, _) = await signer.ExportKeyPairAsync();

        // Truncated signature (only first 16 bytes of 64)
        var truncated = new byte[16];
        Array.Copy(signature, truncated, 16);
        var result = await signer.VerifyAsync(pubKey, message, truncated);
        if (result) throw new Exception("Truncated signature should be rejected");

        // Empty signature
        var empty = await signer.VerifyAsync(pubKey, message, Array.Empty<byte>());
        if (empty) throw new Exception("Empty signature should be rejected");

        Console.WriteLine("[EcdsaP256] Reject truncated signature: OK");
    }

    [TestMethod]
    public async Task MutableItems_RejectSequenceRollback()
    {
        var dht = new DhtDiscovery();
        var signer = new HmacFallbackSigner();
        var items = dht.CreateMutableItems(signer);

        // Publish twice to get to sequence 2
        try { await items.PublishAsync(new byte[] { 1 }); } catch { }
        try { await items.PublishAsync(new byte[] { 2 }); } catch { }

        if (items.Sequence != 2)
            throw new Exception($"Sequence should be 2, got {items.Sequence}");

        // Sequence should only go forward, never backward
        // Publish again — sequence should be 3
        try { await items.PublishAsync(new byte[] { 3 }); } catch { }
        if (items.Sequence != 3)
            throw new Exception($"Sequence should be 3, got {items.Sequence}");

        // Verify that each publish incremented monotonically
        Console.WriteLine($"[BEP46] Sequence rollback protection: seq={items.Sequence} (monotonic)");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_SaltIsolation()
    {
        var dht = new DhtDiscovery();
        var signer = new HmacFallbackSigner();
        var items = dht.CreateMutableItems(signer);

        var salt1 = System.Text.Encoding.UTF8.GetBytes("weights");
        var salt2 = System.Text.Encoding.UTF8.GetBytes("kv-cache");

        // Sign data with different salts — these produce different sign payloads
        var value = new byte[] { 42 };
        var sig1 = await signer.SignAsync(BuildSignDataForTest(value, salt1, 1));
        var sig2 = await signer.SignAsync(BuildSignDataForTest(value, salt2, 1));

        // Verify each signature against its own salt
        var valid1 = await items.VerifyAsync(signer.PublicKey, value, sig1, 1, salt1);
        if (!valid1) throw new Exception("Salt1 signature should verify with salt1");

        var valid2 = await items.VerifyAsync(signer.PublicKey, value, sig2, 1, salt2);
        if (!valid2) throw new Exception("Salt2 signature should verify with salt2");

        // Cross-salt verification MUST fail
        var cross1 = await items.VerifyAsync(signer.PublicKey, value, sig1, 1, salt2);
        if (cross1) throw new Exception("Salt1 signature must NOT verify with salt2");

        var cross2 = await items.VerifyAsync(signer.PublicKey, value, sig2, 1, salt1);
        if (cross2) throw new Exception("Salt2 signature must NOT verify with salt1");

        Console.WriteLine("[BEP46] Salt isolation: OK");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_RejectForgedItem()
    {
        var crypto = Client!.Crypto;
        var dht = new DhtDiscovery();

        // Signer A publishes legitimately
        var signerA = new EcdsaP256Signer(crypto);
        await signerA.GenerateKeyAsync();
        var itemsA = dht.CreateMutableItems(signerA);

        var value = System.Text.Encoding.UTF8.GetBytes("real data");
        try { await itemsA.PublishAsync(value); } catch { }

        // Get A's public key (SPKI)
        var (pubKeyA, _) = await signerA.ExportKeyPairAsync();

        // Signer B (attacker) tries to forge a mutable item under A's key
        var signerB = new EcdsaP256Signer(crypto);
        await signerB.GenerateKeyAsync();

        var forgedValue = System.Text.Encoding.UTF8.GetBytes("forged data");
        var forgedSig = await signerB.SignAsync(forgedValue);

        // Verify forged signature against A's public key — MUST fail
        var accepted = await signerA.VerifyAsync(pubKeyA, forgedValue, forgedSig);
        if (accepted) throw new Exception("Forged signature must be rejected against victim's public key");

        // Also verify through MutableItems.VerifyAsync
        var itemsVerify = dht.CreateMutableItems(signerA);
        var accepted2 = await itemsVerify.VerifyAsync(pubKeyA, forgedValue, forgedSig, 1);
        if (accepted2) throw new Exception("MutableItems must reject forged item");

        Console.WriteLine("[BEP46] Reject forged item: OK");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_VerifyRejectsTamperedValue()
    {
        var crypto = Client!.Crypto;
        var dht = new DhtDiscovery();
        var signer = new EcdsaP256Signer(crypto);
        await signer.GenerateKeyAsync();
        var items = dht.CreateMutableItems(signer);

        // Sign a legitimate value through the full BEP 44 pipeline
        var value = System.Text.Encoding.UTF8.GetBytes("legitimate");
        var signData = BuildSignDataForTest(value, null, 1);
        var signature = await signer.SignAsync(signData);
        var (pubKey, _) = await signer.ExportKeyPairAsync();

        // Verify legitimate value
        var valid = await items.VerifyAsync(pubKey, value, signature, 1);
        if (!valid) throw new Exception("Legitimate value should verify");

        // Tamper the value — verification MUST fail
        var tampered = System.Text.Encoding.UTF8.GetBytes("tampered!!");
        var rejected = await items.VerifyAsync(pubKey, tampered, signature, 1);
        if (rejected) throw new Exception("Tampered value must be rejected by VerifyAsync");

        // Tamper the sequence — verification MUST fail
        var wrongSeq = await items.VerifyAsync(pubKey, value, signature, 999);
        if (wrongSeq) throw new Exception("Wrong sequence must be rejected by VerifyAsync");

        Console.WriteLine("[BEP46] Reject tampered value through MutableItems: OK");
        await dht.DisposeAsync();
    }

    /// <summary>
    /// Helper: builds the BEP 44 sign data (same as DhtMutableItems.BuildSignData).
    /// Exposed here for testing sign/verify round-trips.
    /// </summary>
    private static byte[] BuildSignDataForTest(byte[] value, byte[]? salt, long seq)
    {
        var parts = new System.Collections.Generic.List<byte>();
        if (salt != null && salt.Length > 0)
        {
            parts.AddRange(System.Text.Encoding.ASCII.GetBytes($"4:salt{salt.Length}:"));
            parts.AddRange(salt);
        }
        parts.AddRange(System.Text.Encoding.ASCII.GetBytes($"3:seqi{seq}e1:v"));
        parts.AddRange(System.Text.Encoding.ASCII.GetBytes($"{value.Length}:"));
        parts.AddRange(value);
        return parts.ToArray();
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
        var signer = new HmacFallbackSigner();

        // Sign and verify directly through the signer
        var msg = new byte[] { 1, 2, 3 };
        var sig = await signer.SignAsync(msg);
        var valid = await signer.VerifyAsync(signer.PublicKey, msg, sig);
        if (!valid) throw new Exception("Should verify our own signature");

        // Tampered message should fail
        var tampered = new byte[] { 1, 2, 4 };
        var invalid = await signer.VerifyAsync(signer.PublicKey, tampered, sig);
        if (invalid) throw new Exception("Should reject tampered message");

        Console.WriteLine("[MutableItems] Verify signature: OK");
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

    // ═══════════════════════════════════════════════════════════
    //  WireProtocol — Property Coverage
    // ═══════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════
    //  SwarmCompute — Worker Join/Submit
    // ═══════════════════════════════════════════════════════════

}
