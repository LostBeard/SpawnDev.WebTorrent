using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 46 real-propagation tests using two in-process <see cref="DhtDiscovery"/>
/// instances wired to each other via loopback UDP. Publisher signs a value with
/// a real Ed25519 keypair, DHT network-delivers it through a standard BEP 44 put,
/// subscriber's <see cref="DhtMutableItems.OnValueUpdated"/> fires with the same
/// bytes + signature + sequence. Proves BEP 44/46 end-to-end through the actual
/// UDP wire, not just the in-process event wiring that the other BEP 46 tests
/// stub with <c>NotifyMutableUpdate</c>.
///
/// Desktop-only: browser has no UDP sockets.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod(RetryCount = 2)]
    public async Task Bep46_Loopback_PublishSubscribe_DeliversValue()
    {
        if (OperatingSystem.IsBrowser()) return; // Browser has no UDP

        // Randomize ports per invocation to avoid port-reuse races on retry +
        // collisions with any prior testhost instance that hasn't fully released
        // the socket (Windows TIME_WAIT can hold ports for ~120s under high
        // retry-driven churn in a single run).
        var basePort = 40000 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000);
        int publisherPort = basePort;
        int subscriberPort = basePort + 1;


        // Ed25519 signer via DotNetCrypto (desktop platform crypto).
        var crypto = PortableCrypto.CreateCrypto();
        var signer = new Ed25519Signer(crypto);
        await signer.GenerateKeyAsync();
        if (signer.PublicKey.Length != 32)
            throw new Exception($"Ed25519Signer.PublicKey should be 32 raw bytes, got {signer.PublicKey.Length}");

        // No BootstrapNodes - manual FindNodeAsync below handles the loopback
        // meet-up deterministically; the auto-bootstrap has a timing hole in the
        // 2-instance case (one side sends find_node before the other is listening).
        var publisherDht = new DhtDiscovery(new DhtOptions { Port = publisherPort });
        var subscriberDht = new DhtDiscovery(new DhtOptions { Port = subscriberPort });

        var pubWarnings = new List<string>();
        var subWarnings = new List<string>();
        publisherDht.OnWarning += w => pubWarnings.Add(w);
        subscriberDht.OnWarning += w => subWarnings.Add(w);

        try
        {
            var dummy = new byte[20];
            await Task.WhenAll(
                publisherDht.StartAsync(dummy, publisherPort),
                subscriberDht.StartAsync(dummy, subscriberPort));

            if (!publisherDht.IsReady) throw new Exception($"Publisher DHT failed to start. Warnings: {string.Join("; ", pubWarnings)}");
            if (!subscriberDht.IsReady) throw new Exception($"Subscriber DHT failed to start. Warnings: {string.Join("; ", subWarnings)}");

            // Bidirectional find_node exchange. Each side learns the other's NodeId
            // from the response (populating its routing table via HandleResponse) and
            // is also learned by the remote via HandleQuery's BEP 5 add-sender logic.
            var pubEp = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, publisherPort);
            var subEp = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, subscriberPort);
            await publisherDht.FindNodeAsync(subEp);
            await subscriberDht.FindNodeAsync(pubEp);
            // Give both receive loops a moment to process the cross-traffic.
            await Task.Delay(500);

            var bootDeadline = DateTime.UtcNow.AddSeconds(10);
            while ((publisherDht.NodeCount == 0 || subscriberDht.NodeCount == 0)
                && DateTime.UtcNow < bootDeadline)
                await Task.Delay(200);

            if (publisherDht.NodeCount == 0 || subscriberDht.NodeCount == 0)
                throw new Exception(
                    $"DHT loopback bootstrap failed: pub={publisherDht.NodeCount} sub={subscriberDht.NodeCount} nodes after 10s. " +
                    $"Publisher warnings: [{string.Join("; ", pubWarnings)}]. " +
                    $"Subscriber warnings: [{string.Join("; ", subWarnings)}].");

            var publisherItems = publisherDht.CreateMutableItems(signer);
            var subscriberItems = subscriberDht.CreateMutableItems(signer);

            var valueToPublish = new byte[20];
            System.Security.Cryptography.RandomNumberGenerator.Fill(valueToPublish);

            var received = new TaskCompletionSource<(byte[] value, long seq)>();
            subscriberItems.OnValueUpdated += (pk, value, seq) =>
            {
                if (pk.SequenceEqual(signer.PublicKey))
                    received.TrySetResult((value, seq));
            };

            var subCts = new CancellationTokenSource();
            _ = Task.Run(() => subscriberItems.SubscribeAsync(
                signer.PublicKey, salt: null, pollIntervalMs: 1000, ct: subCts.Token));

            // Give subscriber a chance to issue initial GET (which acquires a token
            // that publisher's PUT will need to respond to).
            await Task.Delay(500);

            await publisherItems.PublishAsync(valueToPublish, salt: null);

            var done = await Task.WhenAny(received.Task, Task.Delay(15000));
            subCts.Cancel();

            if (done != received.Task)
                throw new Exception("BEP 46 loopback propagation timed out after 15s - publisher PUT did not reach subscriber GET");

            var (rxValue, rxSeq) = await received.Task;
            if (!rxValue.SequenceEqual(valueToPublish))
                throw new Exception($"Received value mismatch: expected {Convert.ToHexString(valueToPublish)}, got {Convert.ToHexString(rxValue)}");
            if (rxSeq != publisherItems.Sequence)
                throw new Exception($"Received seq={rxSeq}, expected {publisherItems.Sequence}");
        }
        finally
        {
            await publisherDht.DisposeAsync();
            await subscriberDht.DisposeAsync();
        }
    }

    [TestMethod(RetryCount = 2)]
    public async Task Bep46_Loopback_Republish_BumpsSequence()
    {
        if (OperatingSystem.IsBrowser()) return;

        // Randomize ports (see sibling test's comment on port-reuse races).
        var basePort = 40000 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000);
        int publisherPort = basePort;
        int subscriberPort = basePort + 1;

        var prevVerbose = WebTorrentClient.VerboseLogging;
        WebTorrentClient.VerboseLogging = true;

        var crypto = PortableCrypto.CreateCrypto();
        var signer = new Ed25519Signer(crypto);
        await signer.GenerateKeyAsync();

        var publisherDht = new DhtDiscovery(new DhtOptions { Port = publisherPort });
        var subscriberDht = new DhtDiscovery(new DhtOptions { Port = subscriberPort });

        try
        {
            var dummy = new byte[20];
            var pubStart = publisherDht.StartAsync(dummy, publisherPort);
            var subStart = subscriberDht.StartAsync(dummy, subscriberPort);
            await Task.WhenAll(pubStart, subStart);

            var pubEp = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, publisherPort);
            var subEp = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, subscriberPort);
            await publisherDht.FindNodeAsync(subEp);
            await subscriberDht.FindNodeAsync(pubEp);

            var bootDeadline = DateTime.UtcNow.AddSeconds(10);
            while ((publisherDht.NodeCount == 0 || subscriberDht.NodeCount == 0)
                && DateTime.UtcNow < bootDeadline)
                await Task.Delay(200);

            var publisherItems = publisherDht.CreateMutableItems(signer);
            var subscriberItems = subscriberDht.CreateMutableItems(signer);

            var v1 = new byte[20]; System.Security.Cryptography.RandomNumberGenerator.Fill(v1);
            var v2 = new byte[20]; System.Security.Cryptography.RandomNumberGenerator.Fill(v2);

            var received = new List<(byte[] value, long seq)>();
            var gate = new SemaphoreSlim(0);
            subscriberItems.OnValueUpdated += (pk, value, seq) =>
            {
                if (pk.SequenceEqual(signer.PublicKey))
                {
                    lock (received) received.Add((value, seq));
                    gate.Release();
                }
            };

            var subCts = new CancellationTokenSource();
            _ = Task.Run(() => subscriberItems.SubscribeAsync(
                signer.PublicKey, salt: null, pollIntervalMs: 1000, ct: subCts.Token));
            await Task.Delay(500);

            await publisherItems.PublishAsync(v1);
            if (!await gate.WaitAsync(10000)) throw new Exception("First publish did not propagate within 10s");
            var firstSeq = publisherItems.Sequence;

            await Task.Delay(500);
            await publisherItems.PublishAsync(v2);
            if (!await gate.WaitAsync(10000)) throw new Exception("Second publish did not propagate within 10s");
            var secondSeq = publisherItems.Sequence;

            subCts.Cancel();

            if (secondSeq <= firstSeq)
                throw new Exception($"Republish must bump sequence: first={firstSeq}, second={secondSeq}");

            lock (received)
            {
                var v1Got = received.Any(r => r.value.SequenceEqual(v1));
                var v2Got = received.Any(r => r.value.SequenceEqual(v2));
                if (!v1Got) throw new Exception("First value never arrived at subscriber");
                if (!v2Got) throw new Exception("Second value never arrived at subscriber");
            }
        }
        finally
        {
            await publisherDht.DisposeAsync();
            await subscriberDht.DisposeAsync();
        }
    }
}
