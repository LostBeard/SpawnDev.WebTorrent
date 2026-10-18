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
}
