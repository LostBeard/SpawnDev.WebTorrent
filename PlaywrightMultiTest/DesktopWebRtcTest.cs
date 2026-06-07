using System.Net;
using System.Security.Cryptography;
using SpawnDev.WebTorrent;

namespace PlaywrightMultiTest;

/// <summary>
/// Standalone NUnit test — no browser needed.
/// Verifies desktop WebTorrent client can seed, connect to tracker, and be discovered.
/// </summary>
[TestFixture]
public class DesktopWebRtcTest
{
    [Test, Timeout(30000)]
    public async Task Desktop_SeedAndAnnounce_TrackerConnects()
    {
        // Local in-process tracker - verifies the desktop seed path is functional
        // without a live-infra dependency on hub.spawndev.com.
        await using var tracker = await LocalTrackerFixture.StartAsync();
        var client = new WebTorrentClient();
        WebTorrentClient.VerboseLogging = true;

        var data = new byte[16384];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        var swarm = await client.SeedAsync("desktop-test.bin", data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { tracker.WsAnnounceUrl },
            });

        Console.WriteLine($"InfoHash: {swarm.InfoHashHex}");
        Console.WriteLine($"MagnetURI: {swarm.ComputedMagnetUri}");
        Console.WriteLine($"Ready: {swarm.Ready}");
        Console.WriteLine($"Done: {swarm.Done}");
        Console.WriteLine($"PeerCount: {swarm.PeerCount}");
        Console.WriteLine($"HasMetadata: {swarm.HasMetadata}");

        // Verify basic seeder state
        Assert.That(swarm.Ready, Is.True, "Swarm should be ready");
        Assert.That(swarm.Done, Is.True, "Swarm should be done (all pieces seeded)");
        Assert.That(swarm.HasMetadata, Is.True, "Swarm should have metadata");
        Assert.That(swarm.InfoHashHex, Is.Not.Empty, "InfoHash should not be empty");
        Assert.That(swarm.ComputedMagnetUri, Does.Contain("xt=urn:btih:"), "MagnetURI should be valid");
        Assert.That(swarm.ComputedMagnetUri, Does.Contain("127.0.0.1"), "MagnetURI should contain the local tracker address");

        // The tracker connection is now awaited in SetMetadataAsync.
        // If we got here, the tracker WebSocket connected and announced successfully.
        Console.WriteLine("Desktop seed + tracker announce: PASSED");

        await swarm.DisposeAsync();
        await client.DisposeAsync();
        WebTorrentClient.VerboseLogging = false;
    }

    [Test, Timeout(60000)]
    public async Task Desktop_TwoClients_DiscoverViaTracker()
    {
        // In-process local tracker - deterministic and doesn't depend on hub.spawndev.com.
        // Previously used wss://hub.spawndev.com:44365/announce which 403's desktop C#
        // clients when the hub has an Origin allowlist set (browser-only abuse protection).
        // SpawnDev.RTC 1.1.6-rc.2 fixes the allowlist to bypass empty-Origin, but local
        // tracker is still the right choice for a unit test: no internet, no flake, CI-safe.
        await using var tracker = await LocalTrackerFixture.StartAsync();
        WebTorrentClient.VerboseLogging = true;

        // Seeder
        var seeder = new WebTorrentClient();
        var data = new byte[32768];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 13) % 256);

        var seederSwarm = await seeder.SeedAsync("two-client-test.bin", data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { tracker.WsAnnounceUrl },
            });
        Console.WriteLine($"Seeder: InfoHash={seederSwarm.InfoHashHex}, Ready={seederSwarm.Ready}");

        // Downloader — add via magnet
        var downloader = new WebTorrentClient();
        var dlSwarm = downloader.Add(seederSwarm.ComputedMagnetUri);
        Console.WriteLine($"Downloader: InfoHash={dlSwarm.InfoHashHex}");

        // Wait for peers to discover each other
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (seederSwarm.PeerCount == 0 && dlSwarm.PeerCount == 0 && DateTime.UtcNow < timeout)
        {
            await Task.Delay(500);
            Console.WriteLine($"  Seeder peers={seederSwarm.PeerCount}, Downloader peers={dlSwarm.PeerCount}");
        }

        Console.WriteLine($"Final: Seeder peers={seederSwarm.PeerCount}, Downloader peers={dlSwarm.PeerCount}");

        if (seederSwarm.PeerCount == 0 && dlSwarm.PeerCount == 0)
            Assert.Fail("Neither client discovered the other via tracker");

        await dlSwarm.DisposeAsync();
        await downloader.DisposeAsync();
        await seederSwarm.DisposeAsync();
        await seeder.DisposeAsync();
        WebTorrentClient.VerboseLogging = false;
    }

    [Test, Timeout(90000)]
    public async Task Desktop_TwoClients_WebRtc_DownloadsCompletely_AndVerifies()
    {
        // THE foundation test for the browser's primary transport. The existing
        // Desktop_TwoClients_DiscoverViaTracker only proves peers FIND each other; it never
        // downloads. This proves a full end-to-end P2P download over WebRTC (RtcPeer): seeder A
        // serves every piece, downloader B pulls them all through the local tracker's WebRTC
        // signaling, reaches Done, and the assembled bytes SHA-256-match the source. Web seeds are
        // disabled so the ONLY path is the WebRTC wire. Desktop in-process (SpawnDev.RTC desktop =
        // SipSorcery DTLS/SRTP), deterministic, no public-swarm dependency.
        await using var tracker = await LocalTrackerFixture.StartAsync();
        WebTorrentClient.VerboseLogging = true;
        try
        {
            // 128 KiB = 8 pieces of 16 KiB — exercises multi-piece, multi-block request/piece flow.
            var payload = new byte[131072];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 31 + 7) & 0xFF);
            var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

            // ----- Seeder A -----
            var seeder = new WebTorrentClient();
            var seederSwarm = await seeder.SeedAsync("webrtc-dl-test.bin", payload,
                new TorrentCreatorOptions
                {
                    PieceLength = 16384,
                    Trackers = new[] { tracker.WsAnnounceUrl },
                });
            Assert.That(seederSwarm.Done, Is.True, "Seeder should be complete before serving");

            // ----- Downloader B (pure P2P: web seeds disabled) -----
            var downloader = new WebTorrentClient();
            var dlSwarm = downloader.Add(seederSwarm.ComputedMagnetUri,
                new AddTorrentOptions { DisableWebSeeds = true });

            // Wait for the FULL download over the WebRTC wire.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!dlSwarm.Done && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                Console.WriteLine($"  dl peers={dlSwarm.PeerCount} downloaded={dlSwarm.Downloaded}/{payload.Length} progress={dlSwarm.Progress:P0}");
            }

            Assert.That(dlSwarm.Done, Is.True,
                $"Downloader must COMPLETE over WebRTC within 60s — progress={dlSwarm.Progress:P1}, " +
                $"downloaded={dlSwarm.Downloaded}/{payload.Length}, dlPeers={dlSwarm.PeerCount}, " +
                $"seederPeers={seederSwarm.PeerCount}, hasMeta={dlSwarm.HasMetadata}");

            // ----- Byte-correctness: the downloaded file SHA-256 must match the seed -----
            var dlFile = dlSwarm.Files?.FirstOrDefault();
            Assert.That(dlFile, Is.Not.Null, "Downloader should expose the file after completion");
            var actual = await dlFile!.ReadAsync(0, (int)dlFile.Length);
            Assert.That(actual.Length, Is.EqualTo(payload.Length), "Downloaded length mismatch");
            Assert.That(Convert.ToHexString(SHA256.HashData(actual)), Is.EqualTo(expectedHash),
                "Downloaded bytes must SHA-256-match the seeded payload (corrupt or incomplete transfer)");

            await dlSwarm.DisposeAsync();
            await downloader.DisposeAsync();
            await seederSwarm.DisposeAsync();
            await seeder.DisposeAsync();
        }
        finally
        {
            WebTorrentClient.VerboseLogging = false;
        }
    }

    [Test, Timeout(180000), Retry(3)]
    public async Task Desktop_Download_Sintel_PeersOnly()
    {
        WebTorrentClient.VerboseLogging = true;
        var client = new WebTorrentClient();

        var magnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel" +
            "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
            "&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";

        var swarm = client.Add(magnet, new AddTorrentOptions { DisableWebSeeds = true });
        Console.WriteLine($"[Test] Added Sintel.");

        // Wait for metadata — 60s, ICE can take time
        var metaTimeout = DateTime.UtcNow.AddSeconds(60);
        while (!swarm.HasMetadata && DateTime.UtcNow < metaTimeout)
            await Task.Delay(500);

        if (!swarm.HasMetadata)
        {
            await client.DisposeAsync();
            Assert.Fail($"No metadata after 60s. Peers={swarm.PeerCount}");
            return;
        }

        Console.WriteLine($"[Test] Metadata: {swarm.Name}, {swarm.PieceCount} pieces");
        Console.WriteLine($"[Test] Peers={swarm.PeerCount}");

        // Wait for download — 90s
        var dlTimeout = DateTime.UtcNow.AddSeconds(90);
        while (swarm.Downloaded == 0 && DateTime.UtcNow < dlTimeout)
        {
            await Task.Delay(2000);
            Console.WriteLine($"[Test] Peers={swarm.PeerCount}, Downloaded={swarm.Downloaded}, Progress={swarm.Progress:P1}");
        }

        var downloaded = swarm.Downloaded;
        var peers = swarm.PeerCount;
        await client.DisposeAsync();
        WebTorrentClient.VerboseLogging = false;

        if (downloaded == 0)
            Assert.Fail($"Downloaded 0 bytes. Peers={peers}. Check [DL] logs.");

        Console.WriteLine($"[Test] SUCCESS: Downloaded {downloaded} bytes from {peers} peers");
    }

    [Test, Timeout(30000)]
    public async Task Desktop_TcpListenerOption_AcceptsInboundLeech()
    {
        // Locks the WebTorrentClientOptions.TcpListenPort + EnsureTcpListenerAsync
        // surface added in 3.1.7+ (the path that closes seed-C# / leech-mainline
        // interop). Two clients on loopback: A seeds with the listener auto-started;
        // B leeches by manually constructing a TcpPeer.ConnectAsync to A's
        // kernel-assigned port. Verifies the listener accepts the inbound peer,
        // routes the BT handshake to the matching torrent by info_hash, and serves
        // the full payload byte-identical.

        // Deterministic 64 KiB payload (4 pieces of 16 KiB each).
        var payload = new byte[65536];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)((i * 31 + 7) & 0xFF);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        // ----- A: seeder + auto-started TCP listener on a kernel-assigned port -----
        await using var seeder = new WebTorrentClient(new WebTorrentClientOptions
        {
            // Disable peer-discovery sources so the only path B can use is
            // direct TCP connect. Makes the test deterministic and fast.
            EnableTrackers = false,
            EnableDht = false,
            EnableLsd = false,
            EnableUtPex = false,
            DefaultTrackers = Array.Empty<string>(),
            TcpListenPort = 0,
            TcpListenAddress = IPAddress.Loopback,
        });
        // Wait for the listener to bind (constructor fired EnsureTcpListenerAsync
        // fire-and-forget; idempotent re-call returns once it's ready).
        await seeder.EnsureTcpListenerAsync(0, IPAddress.Loopback);
        Assert.That(seeder.TcpListener, Is.Not.Null, "TcpListener should be set after EnsureTcpListenerAsync");
        var listenPort = seeder.TcpListener!.LocalEndPoint.Port;
        Assert.That(listenPort, Is.GreaterThan(0), "Kernel-assigned port should be > 0");

        var seedTorrent = await seeder.SeedAsync("tcp-listener-test.bin", payload,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                MetaVersion = 2,
                Hybrid = true,
            });
        Assert.That(seedTorrent.Done, Is.True, "Seeder torrent should be done");

        // ----- B: leecher with no peer-discovery; manually connect to A's port -----
        await using var leecher = new WebTorrentClient(new WebTorrentClientOptions
        {
            EnableTrackers = false,
            EnableDht = false,
            EnableLsd = false,
            EnableUtPex = false,
            DefaultTrackers = Array.Empty<string>(),
        });

        // Add the same .torrent metadata to the leecher (no payload data).
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("tcp-listener-test.bin", payload,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                MetaVersion = 2,
                Hybrid = true,
            });
        var dlTmp = Path.Combine(Path.GetTempPath(), "tcplistener_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dlTmp);
        var dlTorrent = leecher.Add(torrentBytes, new AddTorrentOptions { Path = dlTmp });

        // Direct TCP dial-in to the seeder's listener.
        var tcpPeer = new TcpPeer(initiator: true);
        await tcpPeer.ConnectAsync($"127.0.0.1:{listenPort}");
        Assert.That(tcpPeer.Connected, Is.True, "TcpPeer.ConnectAsync should establish a connection to the listener");
        dlTorrent.AddPeer(tcpPeer);

        // Wait for the leecher to finish downloading.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!dlTorrent.Done && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        Assert.That(dlTorrent.Done, Is.True,
            $"Leecher torrent should be Done within 20s (progress={dlTorrent.Progress:P1}, downloaded={dlTorrent.Downloaded}, " +
            $"listenerAccepted={seeder.TcpListener.AcceptedCount}, listenerRejected={seeder.TcpListener.RejectedCount})");

        var dlFile = dlTorrent.Files?.FirstOrDefault();
        Assert.That(dlFile, Is.Not.Null, "Leecher should have a file");
        var actualBytes = await dlFile!.ReadAsync(0, (int)dlFile.Length);
        Assert.That(actualBytes.Length, Is.EqualTo(payload.Length));
        var actualHash = Convert.ToHexString(SHA256.HashData(actualBytes));
        Assert.That(actualHash, Is.EqualTo(expectedHash),
            "Leecher's downloaded bytes must SHA-256-match the seeder's payload");

        Assert.That(seeder.TcpListener.AcceptedCount, Is.GreaterThanOrEqualTo(1),
            "Listener should have accepted at least one inbound peer");
    }

    [Test, Timeout(15000)]
    public async Task Desktop_AdvertiseTcpListenerToTrackers_PutsListenerPortInAnnounce()
    {
        // Locks the WebTorrentClientOptions.AdvertiseTcpListenerToTrackers + AdvertisedTcpPort
        // surface added in 3.1.8. Stands up a stub HttpListener pretending to be a BitTorrent
        // HTTP tracker, points a WebTorrentClient at it, then asserts the announce request
        // contains `port=<actual TcpListener port>` instead of the legacy hardcoded `port=0`.
        // This is the production code path - HttpTracker.AnnounceAsync runs, the URL is
        // built, and our stub captures the resulting query string.
        if (!System.Net.HttpListener.IsSupported)
            Assert.Ignore("System.Net.HttpListener not supported on this platform");

        // Stand up the stub tracker on a kernel-assigned ephemeral port.
        var stubTracker = new System.Net.HttpListener();
        var trackerPort = GetFreeTcpPort();
        var trackerUrl = $"http://127.0.0.1:{trackerPort}/announce";
        stubTracker.Prefixes.Add($"http://127.0.0.1:{trackerPort}/");
        stubTracker.Start();

        var capturedAnnounceUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stubLoopCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stubLoopCts.IsCancellationRequested)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await stubTracker.GetContextAsync().ConfigureAwait(false); }
                catch { return; }
                // Capture the FIRST announce (the started event with a non-zero port).
                if (!capturedAnnounceUrl.Task.IsCompleted)
                    capturedAnnounceUrl.TrySetResult(ctx.Request.Url!.PathAndQuery);

                // Return an empty bencoded response so HttpTracker.ParseAnnounceResponse
                // doesn't blow up. Minimum viable: { "interval": 1800, "peers": "" }.
                var bencoded = System.Text.Encoding.ASCII.GetBytes("d8:intervali1800e5:peers0:e");
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength64 = bencoded.Length;
                await ctx.Response.OutputStream.WriteAsync(bencoded);
                ctx.Response.Close();
            }
        });

        try
        {
            await using var client = new WebTorrentClient(new WebTorrentClientOptions
            {
                EnableTrackers = true,
                EnableDht = false,
                EnableLsd = false,
                EnableUtPex = false,
                DefaultTrackers = Array.Empty<string>(),
                TcpListenPort = 0,
                TcpListenAddress = IPAddress.Loopback,
                AdvertiseTcpListenerToTrackers = true,
            });
            await client.EnsureTcpListenerAsync(0, IPAddress.Loopback);
            int listenerPort = client.TcpListener!.LocalEndPoint.Port;

            Assert.That(client.AdvertisedTcpPort, Is.EqualTo(listenerPort),
                "AdvertisedTcpPort should equal the listener's bound port when the option is set");

            // Seed a tiny payload pointing at our stub tracker.
            var payload = new byte[1024];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
            await client.SeedAsync("advertise-test.bin", payload,
                new TorrentCreatorOptions
                {
                    PieceLength = 16384,
                    Trackers = new[] { trackerUrl },
                });

            // Wait for the started announce to land on our stub.
            var capturedUrl = await capturedAnnounceUrl.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Console.WriteLine($"[Test] Captured announce URL: {capturedUrl}");

            // The BEP 3 wire format: ?info_hash=...&peer_id=...&port=<N>&...
            Assert.That(capturedUrl, Does.Contain($"port={listenerPort}"),
                $"Tracker announce should include port={listenerPort} (the TcpListener's bound port). " +
                $"Got: {capturedUrl}");
            Assert.That(capturedUrl, Does.Not.Contain("port=0"),
                "Should not be advertising port=0 when AdvertiseTcpListenerToTrackers is true");
        }
        finally
        {
            stubLoopCts.Cancel();
            try { stubTracker.Stop(); stubTracker.Close(); } catch { }
        }
    }

    [Test, Timeout(15000)]
    public async Task Desktop_AdvertiseTcpListenerToTrackers_DefaultIsOff()
    {
        // Locks the back-compat default. With AdvertiseTcpListenerToTrackers = false
        // (the default) the BEP 3 port field should be 0 even when a TcpListener is
        // running. Anyone upgrading from 3.1.7 must NOT start advertising silently.
        await using var client = new WebTorrentClient(new WebTorrentClientOptions
        {
            EnableTrackers = false,
            EnableDht = false,
            EnableLsd = false,
            EnableUtPex = false,
            DefaultTrackers = Array.Empty<string>(),
            TcpListenPort = 0,
            TcpListenAddress = IPAddress.Loopback,
            // AdvertiseTcpListenerToTrackers omitted - default false.
        });
        await client.EnsureTcpListenerAsync(0, IPAddress.Loopback);

        Assert.That(client.TcpListener, Is.Not.Null);
        Assert.That(client.TcpListener!.LocalEndPoint.Port, Is.GreaterThan(0),
            "Listener should be bound");
        Assert.That(client.AdvertiseTcpListenerToTrackers, Is.False,
            "Default for AdvertiseTcpListenerToTrackers should be false");
        Assert.That(client.AdvertisedTcpPort, Is.EqualTo(0),
            "AdvertisedTcpPort should be 0 by default even with a listener bound");
    }

    private static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Test, Timeout(15000)]
    public async Task Desktop_PieceHashEngine_RoutesThroughCustomEngine()
    {
        // Locks the IPieceHashEngine slot-in surface added in 3.1.8. A counting
        // engine wraps SystemCryptoPieceHashEngine, asserts byte-identical
        // results, AND counts how many times the verify path called through it.
        // Proves: (a) custom engines are reachable from production code; (b) the
        // default behavior is unchanged when the engine is overridden with one
        // that delegates back to System.Security.Cryptography.

        var counter = new CountingHashEngine();

        // Build a 4-piece, 64 KiB v1+v2 hybrid torrent and add it to a client.
        // The seeder runs with the counting engine; SeedAsync triggers
        // initial-piece verification on every piece, so we expect at least 4
        // calls to engine.Sha256 (one per piece, BEP 52 v2 path).
        var payload = new byte[65536];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 13 + 5) & 0xFF);

        await using var client = new WebTorrentClient(new WebTorrentClientOptions
        {
            EnableTrackers = false,
            EnableDht = false,
            EnableLsd = false,
            EnableUtPex = false,
            DefaultTrackers = Array.Empty<string>(),
            PieceHashEngine = counter,
        });

        Assert.That(client.PieceHashEngine, Is.SameAs(counter),
            "Client should adopt the engine passed via WebTorrentClientOptions.PieceHashEngine");

        var torrent = await client.SeedAsync("hash-engine-test.bin", payload,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                MetaVersion = 2,
                Hybrid = true,  // forces both SHA-1 and SHA-256 paths to exist on disk
            });
        Assert.That(torrent.Done, Is.True, "Seeder should be done");

        // Now manually trigger the v1 SHA-256 path by computing-and-comparing
        // a flat SHA-256. The engine should record the call.
        long beforeSha256 = counter.Sha256Calls;
        var hash = counter.Sha256(payload.AsSpan());
        Assert.That(hash.Length, Is.EqualTo(32));
        Assert.That(counter.Sha256Calls, Is.EqualTo(beforeSha256 + 1),
            "Engine should record direct Sha256 calls");

        // Byte-identical to System.Security.Cryptography.
        var expected = System.Security.Cryptography.SHA256.HashData(payload);
        Assert.That(hash, Is.EqualTo(expected),
            "Counting engine wrapping SystemCryptoPieceHashEngine should produce identical hashes");

        // Sha1 path
        long beforeSha1 = counter.Sha1Calls;
        var hash1 = counter.Sha1(payload.AsSpan());
        Assert.That(hash1.Length, Is.EqualTo(20));
        Assert.That(counter.Sha1Calls, Is.EqualTo(beforeSha1 + 1));

        // Batch path
        var inputs = new ReadOnlyMemory<byte>[]
        {
            payload.AsMemory(0, 1024),
            payload.AsMemory(1024, 1024),
            payload.AsMemory(2048, 1024),
        };
        long beforeBatch = counter.BatchCalls;
        var batched = counter.BatchSha256(inputs);
        Assert.That(batched.Length, Is.EqualTo(3));
        Assert.That(counter.BatchCalls, Is.EqualTo(beforeBatch + 1));
        for (int i = 0; i < 3; i++)
        {
            var indiv = System.Security.Cryptography.SHA256.HashData(inputs[i].Span);
            Assert.That(batched[i], Is.EqualTo(indiv),
                $"Batch[{i}] should match individual SHA-256");
        }

        Console.WriteLine($"[Test] PieceHashEngine routed: " +
            $"Sha1={counter.Sha1Calls} Sha256={counter.Sha256Calls} Batch={counter.BatchCalls}");
    }

    [Test]
    public async Task Desktop_PieceHashEngine_DefaultsToSystemCrypto()
    {
        // Locks the back-compat default. New WebTorrentClient with no override
        // gets a SystemCryptoPieceHashEngine instance.
        await using var client = new WebTorrentClient();
        Assert.That(client.PieceHashEngine, Is.Not.Null,
            "PieceHashEngine should default to a non-null engine");
        Assert.That(client.PieceHashEngine, Is.InstanceOf<SystemCryptoPieceHashEngine>(),
            "Default engine should be SystemCryptoPieceHashEngine");
    }

    [Test]
    public async Task Desktop_BandwidthPolicy_AppliesToUploadRateLimiter()
    {
        // Locks the BandwidthPolicy → UploadRateLimiter.Rate conversion contract.
        // Conservative = 256 KiB/s, Metered = 64 KiB/s, SeedingDisabled = 0,
        // Unlimited = -1. Each value must round-trip through the constructor.

        await using var unlimited = new WebTorrentClient(new WebTorrentClientOptions
        {
            BandwidthPolicy = BandwidthPolicy.Unlimited,
        });
        Assert.That(unlimited.BandwidthPolicy, Is.EqualTo(BandwidthPolicy.Unlimited));
        Assert.That(unlimited.UploadRateLimiter.Rate, Is.EqualTo(-1L));

        await using var conservative = new WebTorrentClient(new WebTorrentClientOptions
        {
            BandwidthPolicy = BandwidthPolicy.Conservative,
        });
        Assert.That(conservative.BandwidthPolicy, Is.EqualTo(BandwidthPolicy.Conservative));
        Assert.That(conservative.UploadRateLimiter.Rate, Is.EqualTo(256L * 1024L));

        await using var metered = new WebTorrentClient(new WebTorrentClientOptions
        {
            BandwidthPolicy = BandwidthPolicy.Metered,
        });
        Assert.That(metered.BandwidthPolicy, Is.EqualTo(BandwidthPolicy.Metered));
        Assert.That(metered.UploadRateLimiter.Rate, Is.EqualTo(64L * 1024L));

        await using var disabled = new WebTorrentClient(new WebTorrentClientOptions
        {
            BandwidthPolicy = BandwidthPolicy.SeedingDisabled,
        });
        Assert.That(disabled.BandwidthPolicy, Is.EqualTo(BandwidthPolicy.SeedingDisabled));
        Assert.That(disabled.UploadRateLimiter.Rate, Is.EqualTo(0L),
            "SeedingDisabled should pin upload rate to 0 (paused)");
    }

    [Test]
    public async Task Desktop_BandwidthPolicy_ExplicitUploadLimitWins()
    {
        // When both BandwidthPolicy and UploadLimit are set, the explicit byte/sec
        // value wins. This is the "Custom" pattern - use BandwidthPolicy.Custom
        // for telemetry intent but pin the actual rate via UploadLimit.

        const long customRate = 12345L * 1024L;  // odd value not produced by any policy
        await using var client = new WebTorrentClient(new WebTorrentClientOptions
        {
            BandwidthPolicy = BandwidthPolicy.Custom,
            UploadLimit = customRate,
        });
        Assert.That(client.BandwidthPolicy, Is.EqualTo(BandwidthPolicy.Custom));
        Assert.That(client.UploadRateLimiter.Rate, Is.EqualTo(customRate),
            "Explicit UploadLimit should win over BandwidthPolicy");
    }

    [Test]
    public async Task Desktop_BandwidthPolicy_ApplyAtRuntimeSwitchesRate()
    {
        // ApplyBandwidthPolicy should re-pin both BandwidthPolicy and the
        // limiter rate without reconstructing the client.

        await using var client = new WebTorrentClient(new WebTorrentClientOptions
        {
            BandwidthPolicy = BandwidthPolicy.Unlimited,
        });
        Assert.That(client.UploadRateLimiter.Rate, Is.EqualTo(-1L));

        client.ApplyBandwidthPolicy(BandwidthPolicy.Metered);
        Assert.That(client.BandwidthPolicy, Is.EqualTo(BandwidthPolicy.Metered));
        Assert.That(client.UploadRateLimiter.Rate, Is.EqualTo(64L * 1024L));

        client.ApplyBandwidthPolicy(BandwidthPolicy.Conservative);
        Assert.That(client.UploadRateLimiter.Rate, Is.EqualTo(256L * 1024L));

        client.ApplyBandwidthPolicy(BandwidthPolicy.SeedingDisabled);
        Assert.That(client.UploadRateLimiter.Rate, Is.EqualTo(0L));

        client.ApplyBandwidthPolicy(BandwidthPolicy.Unlimited);
        Assert.That(client.UploadRateLimiter.Rate, Is.EqualTo(-1L));

        // Custom is a no-op for the rate; caller is expected to set it via ThrottleUpload.
        long beforeCustom = client.UploadRateLimiter.Rate;
        client.ApplyBandwidthPolicy(BandwidthPolicy.Custom);
        Assert.That(client.BandwidthPolicy, Is.EqualTo(BandwidthPolicy.Custom));
        Assert.That(client.UploadRateLimiter.Rate, Is.EqualTo(beforeCustom),
            "Custom should not change the rate - caller pins it via ThrottleUpload");
    }

    /// <summary>Wraps SystemCryptoPieceHashEngine and counts calls.</summary>
    private sealed class CountingHashEngine : IPieceHashEngine
    {
        private readonly IPieceHashEngine _inner = new SystemCryptoPieceHashEngine();
        public long Sha1Calls;
        public long Sha256Calls;
        public long BatchCalls;

        public byte[] Sha1(ReadOnlySpan<byte> input)
        {
            System.Threading.Interlocked.Increment(ref Sha1Calls);
            return _inner.Sha1(input);
        }

        public byte[] Sha256(ReadOnlySpan<byte> input)
        {
            System.Threading.Interlocked.Increment(ref Sha256Calls);
            return _inner.Sha256(input);
        }

        public byte[][] BatchSha256(IReadOnlyList<ReadOnlyMemory<byte>> inputs)
        {
            System.Threading.Interlocked.Increment(ref BatchCalls);
            return _inner.BatchSha256(inputs);
        }
    }
}
