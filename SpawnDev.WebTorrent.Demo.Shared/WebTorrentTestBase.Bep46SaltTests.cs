using SpawnDev.SpawnJS.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 46 with SALTS: one Ed25519 key publishes several independent torrents, each under its own salt (the usual way to publish more than one thing per key). A BEP 44 get response
/// carries v, k, seq and sig but not the salt, which is part of what was signed, so a subscriber has to verify with the salt it asked for; and the values it holds have to be kept
/// per (key, salt), not per key. Before the fix every salted item failed verification on the GET side (the signature was checked as if there were no salt) and a second item under
/// the same key would have replaced the first in the cache.
/// Desktop-only: browser has no UDP sockets.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private static async Task<(DhtDiscovery publisher, DhtDiscovery subscriber)> StartLoopbackDhtPair()
    {
        var basePort = 40000 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000);
        var publisherDht = new DhtDiscovery(new DhtOptions { Port = basePort, BootstrapNodes = Array.Empty<string>() });
        var subscriberDht = new DhtDiscovery(new DhtOptions { Port = basePort + 1, BootstrapNodes = Array.Empty<string>() });
        var dummy = new byte[20];
        await Task.WhenAll(publisherDht.StartAsync(dummy, basePort), subscriberDht.StartAsync(dummy, basePort + 1));
        await publisherDht.FindNodeAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, basePort + 1));
        await subscriberDht.FindNodeAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, basePort));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((publisherDht.NodeCount == 0 || subscriberDht.NodeCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(100);
        if (publisherDht.NodeCount == 0 || subscriberDht.NodeCount == 0) throw new Exception("DHT loopback bootstrap failed");
        return (publisherDht, subscriberDht);
    }

    [TestMethod(RetryCount = 2)]
    public async Task Bep46_Loopback_SaltedItems_UnderOneKey_AreFoundSeparatelyAndVerified()
    {
        if (OperatingSystem.IsBrowser()) return; // Browser has no UDP

        var signer = new Ed25519Signer(PortableCrypto.CreateCrypto());
        await signer.GenerateKeyAsync();
        var (publisherDht, subscriberDht) = await StartLoopbackDhtPair();
        try
        {
            var publisher = publisherDht.CreateMutableItems(signer);
            var saltA = new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
            var saltB = new byte[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 };
            var valueA = Enumerable.Repeat((byte)0xA1, 20).ToArray();
            var valueB = Enumerable.Repeat((byte)0xB2, 20).ToArray();
            await publisher.PublishAsync(valueA, saltA);
            await publisher.PublishAsync(valueB, saltB);

            // a subscriber that has only the public key (it cannot sign) asks for each item with its salt
            var subscriber = subscriberDht.CreateMutableItems(await ReadOnlyEd25519Verifier.CreateAsync(PortableCrypto.CreateCrypto(), signer.PublicKey));
            var updates = new System.Collections.Concurrent.ConcurrentBag<(byte[]? salt, byte[] value)>();
            subscriber.OnItemUpdated += (_, salt, value, _) => updates.Add((salt, value));

            var gotA = await subscriber.GetAsync(signer.PublicKey, saltA);
            if (gotA == null) throw new Exception("the item under salt A was not found: a salted get response must verify with the salt that was asked for");
            if (!gotA.Value.value.SequenceEqual(valueA)) throw new Exception($"salt A returned {Convert.ToHexString(gotA.Value.value)}");

            var gotB = await subscriber.GetAsync(signer.PublicKey, saltB);
            if (gotB == null) throw new Exception("the item under salt B was not found");
            if (!gotB.Value.value.SequenceEqual(valueB)) throw new Exception($"salt B returned {Convert.ToHexString(gotB.Value.value)}: items under one key replaced each other");

            var gotC = await subscriber.GetAsync(signer.PublicKey, new byte[] { 3, 3, 3, 3 });
            if (gotC != null) throw new Exception("a salt nobody published under returned a value");

            if (!updates.Any(u => u.salt != null && u.salt.SequenceEqual(saltA) && u.value.SequenceEqual(valueA)) ||
                !updates.Any(u => u.salt != null && u.salt.SequenceEqual(saltB) && u.value.SequenceEqual(valueB)))
                throw new Exception("OnItemUpdated did not report each item with its own salt");
        }
        finally
        {
            await publisherDht.DisposeAsync();
            await subscriberDht.DisposeAsync();
        }
    }

    [TestMethod(RetryCount = 2)]
    public async Task Bep46_Loopback_Chain_LookupWalksThroughNodesItWasNeverIntroducedTo()
    {
        if (OperatingSystem.IsBrowser()) return;

        // Twelve DHT nodes in a line, each introduced only to its neighbours. The publisher is at one end, the subscriber at the other, and the subscriber has never heard of the nodes
        // that hold the item: it has to walk toward the target through the answers it gets (each answer names closer nodes). A lookup that asks its first few guesses and stops
        // (the old one: 3 nodes, 3 rounds) finds nothing here, as it would on the real DHT.
        var signer = new Ed25519Signer(PortableCrypto.CreateCrypto());
        await signer.GenerateKeyAsync();
        int basePort = 40000 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(19000);
        var nodes = new List<DhtDiscovery>();
        try
        {
            for (int i = 0; i < 12; i++)
            {
                var node = new DhtDiscovery(new DhtOptions { Port = basePort + i, BootstrapNodes = Array.Empty<string>() });
                await node.StartAsync(new byte[20], basePort + i);
                nodes.Add(node);
            }
            var verifier = await ReadOnlyEd25519Verifier.CreateAsync(PortableCrypto.CreateCrypto(), signer.PublicKey);
            var holders = nodes.Select(n => n.CreateMutableItems(verifier)).ToList(); // every node serves get/put for what it is given
            // introduce every neighbouring pair AT ONCE, while all the tables are still empty: one after another, each new node learns the previous node's whole table (which holds
            // everything before it) and the "line" collapses into a clique
            var introductions = new List<Task>();
            for (int i = 0; i + 1 < nodes.Count; i++)
            {
                introductions.Add(nodes[i].FindNodeAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, basePort + i + 1)));
                introductions.Add(nodes[i + 1].FindNodeAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, basePort + i)));
            }
            await Task.WhenAll(introductions);
            await Task.Delay(1500);
            if (nodes[11].NodeCount > 4) throw new Exception($"the far node knows {nodes[11].NodeCount} of 12 nodes: it is not far from the others");

            var publisher = nodes[0].CreateMutableItems(signer);
            publisher.EnsureSequenceAtLeast(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var salt = Enumerable.Repeat((byte)5, 16).ToArray();
            var value = Enumerable.Repeat((byte)0x5A, 20).ToArray();
            await publisher.PublishAsync(value, salt);

            var subscriber = nodes[11].CreateMutableItems(verifier);
            var got = await subscriber.GetAsync(signer.PublicKey, salt);
            if (got == null) throw new Exception($"the item was not found from the far end of a 12-node line (far node knows {nodes[11].NodeCount} nodes after the lookup, publisher sent {publisher.PutsSent} puts)");
            if (!got.Value.value.SequenceEqual(value)) throw new Exception("found a different value");
        }
        finally
        {
            foreach (var node in nodes) await node.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Bep46_EnsureSequenceAtLeast_RaisesNeverLowers_AndPublishGoesAboveIt()
    {
        // A publisher with no persistent storage starts every process at sequence 1, and a DHT node ignores a put that is not higher than what it holds, so a restarted
        // publisher's updates vanish. The caller can raise the floor (the Unix time in MILLISECONDS rises across restarts without storing anything); it never lowers it.
        var signer = new Ed25519Signer(PortableCrypto.CreateCrypto());
        await signer.GenerateKeyAsync();
        var dht = new DhtDiscovery(new DhtOptions { Port = 40000 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000), BootstrapNodes = Array.Empty<string>() });
        try
        {
            using var items = dht.CreateMutableItems(signer);
            if (items.Sequence != 0) throw new Exception($"a new publisher starts at {items.Sequence}, expected 0");
            long floor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            items.EnsureSequenceAtLeast(floor);
            if (items.Sequence != floor) throw new Exception($"sequence {items.Sequence}, expected {floor}");
            items.EnsureSequenceAtLeast(5);
            items.EnsureSequenceAtLeast(floor - 1);
            if (items.Sequence != floor) throw new Exception($"EnsureSequenceAtLeast lowered the sequence to {items.Sequence}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await items.PublishAsync(new byte[20], new byte[16], cts.Token); } catch (OperationCanceledException) { } // no peers to put to: only the sequence matters here
            if (items.Sequence != floor + 1) throw new Exception($"a publish should use {floor + 1}, the sequence is {items.Sequence}");
        }
        finally { await dht.DisposeAsync(); }
    }

    [TestMethod(RetryCount = 2)]
    public async Task Bep46_Loopback_SaltedItem_Republish_IsSeenByANewSubscriber_WithTheNewValue()
    {
        if (OperatingSystem.IsBrowser()) return;

        var signer = new Ed25519Signer(PortableCrypto.CreateCrypto());
        await signer.GenerateKeyAsync();
        var (publisherDht, subscriberDht) = await StartLoopbackDhtPair();
        try
        {
            var publisher = publisherDht.CreateMutableItems(signer);
            var salt = Enumerable.Repeat((byte)7, 16).ToArray();
            var first = Enumerable.Repeat((byte)0x11, 20).ToArray();
            var second = Enumerable.Repeat((byte)0x22, 20).ToArray();
            await publisher.PublishAsync(first, salt);
            await publisher.PublishAsync(second, salt);

            var subscriber = subscriberDht.CreateMutableItems(await ReadOnlyEd25519Verifier.CreateAsync(PortableCrypto.CreateCrypto(), signer.PublicKey));
            var got = await subscriber.GetAsync(signer.PublicKey, salt);
            if (got == null) throw new Exception("the republished salted item was not found");
            if (!got.Value.value.SequenceEqual(second)) throw new Exception($"got {Convert.ToHexString(got.Value.value)}, expected the second value");
            if (got.Value.sequence != publisher.Sequence) throw new Exception($"sequence {got.Value.sequence}, expected {publisher.Sequence}");
        }
        finally
        {
            await publisherDht.DisposeAsync();
            await subscriberDht.DisposeAsync();
        }
    }

    [TestMethod(RetryCount = 2)]
    public async Task Bep46_Loopback_RepublishAsync_KeepsSomeoneElsesItemAliveAfterItsCreatorIsGone()
    {
        if (OperatingSystem.IsBrowser()) return;

        // A publishes a salted item. B sees it (a get, which carries the creator's signature) and later hands it on with RepublishAsync, though B cannot sign for A. After A AND B are
        // gone a node that only knows C still finds the item: B wrote it to C. Without the republish nobody but A held it. (Both nodes going away also checks that a node keeps
        // listening after sending to a dead one: on Windows the ICMP reply used to kill the receive loop.)
        var signer = new Ed25519Signer(PortableCrypto.CreateCrypto());
        await signer.GenerateKeyAsync();
        int basePort = 40000 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(19000);
        var nodes = new List<DhtDiscovery>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var node = new DhtDiscovery(new DhtOptions { Port = basePort + i, BootstrapNodes = Array.Empty<string>() });
                await node.StartAsync(new byte[20], basePort + i);
                nodes.Add(node);
            }
            var (a, b, c, d) = (nodes[0], nodes[1], nodes[2], nodes[3]);
            async Task Introduce(DhtDiscovery x, int xi, DhtDiscovery y, int yi)
            {
                await x.FindNodeAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, basePort + yi));
                await y.FindNodeAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, basePort + xi));
            }
            await Introduce(a, 0, b, 1); await Introduce(a, 0, c, 2); await Introduce(b, 1, c, 2);
            await Task.Delay(500);

            var salt = Enumerable.Repeat((byte)9, 16).ToArray();
            var value = Enumerable.Repeat((byte)0x77, 20).ToArray();
            var publisher = a.CreateMutableItems(signer);
            publisher.EnsureSequenceAtLeast(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await publisher.PublishAsync(value, salt);          // B and C have no items instance yet, so they keep nothing of this

            var verifier = await ReadOnlyEd25519Verifier.CreateAsync(PortableCrypto.CreateCrypto(), signer.PublicKey);
            var relay = b.CreateMutableItems(verifier);
            var seen = await relay.GetAsync(signer.PublicKey, salt);
            if (seen == null) throw new Exception("the relay did not see the item while its creator was up");

            await a.DisposeAsync();                              // the creator goes offline
            nodes.Remove(a);

            var holder = c.CreateMutableItems(verifier);         // C starts serving get/put now
            if (await relay.RepublishAsync(signer.PublicKey, salt, shouldHandOn: v => false)) throw new Exception("RepublishAsync handed on an item its caller declined");
            if (!await relay.RepublishAsync(signer.PublicKey, salt)) throw new Exception("RepublishAsync reported that it handed the item on to nobody");

            await b.DisposeAsync();                                // the relay goes too: now only what it WROTE to C is left
            nodes.Remove(b);
            await Introduce(c, 2, d, 3);                          // D knows only C
            var late = d.CreateMutableItems(verifier);
            var found = await late.GetAsync(signer.PublicKey, salt);
            if (found == null) throw new Exception("after the creator and the relay left, a node that knows only C could not find the item: the republish did not reach C");
            if (!found.Value.value.SequenceEqual(value)) throw new Exception("found a different value");

            // an item this node never saw cannot be handed on
            if (await relay.RepublishAsync(signer.PublicKey, new byte[] { 1, 2, 3 }, refresh: false)) throw new Exception("republished an item it never saw");
        }
        finally
        {
            foreach (var node in nodes) await node.DisposeAsync();
        }
    }
}
