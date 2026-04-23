using System.Security.Cryptography;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for <see cref="V2HashRequestCoordinator"/> - the BEP 52 hash_request / hashes /
/// hash_reject correlation state machine. Covers the happy path (request -&gt; response
/// -&gt; verified result), rejection path (hash_reject yields HashRejectedException),
/// timeout path (stuck request yields TimeoutException), verification failure path
/// (peer returns cryptographically inconsistent data), cancellation, and unsolicited-
/// response drop.
/// </summary>
[TestFixture]
public class V2HashRequestCoordinatorTests
{
    // Small helper that builds a valid 4-leaf Merkle tree so tests can construct real
    // verifiable hashes responses.
    private static (byte[] root, byte[][] leaves, byte h23) Build4LeafTree()
    {
        var leaves = Enumerable.Range(0, 4).Select(i => SHA256.HashData(new byte[] { (byte)i })).ToArray();
        return (HashPair(HashPair(leaves[0], leaves[1]), HashPair(leaves[2], leaves[3])), leaves, 0);
    }

    private static byte[] HashPair(byte[] l, byte[] r)
    {
        var b = new byte[64];
        l.CopyTo(b, 0);
        r.CopyTo(b, 32);
        return SHA256.HashData(b);
    }

    [Test]
    public async Task RequestAsync_ReturnsHashes_WhenPeerRespondsWithVerifiedData()
    {
        var leaves = Enumerable.Range(0, 4).Select(i => SHA256.HashData(new byte[] { (byte)i })).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var root = HashPair(h01, h23);

        var coord = new V2HashRequestCoordinator();
        Bep52WireMessages.HashRequest? sentReq = null;
        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: r => { sentReq = r; return Task.CompletedTask; });

        Assert.That(sentReq, Is.Not.Null, "Send callback must run synchronously inside RequestAsync");

        // Simulate peer response. Length=2, ProofLayers=1, HashList = [l0, l1, h23].
        coord.HandleHashes(new Bep52WireMessages.Hashes(
            root, 0, 0, 2, 1,
            new[] { leaves[0], leaves[1], h23 }));

        var result = await reqTask;
        Assert.That(result.Length, Is.EqualTo(3));
        Assert.That(coord.OutstandingCount, Is.EqualTo(0));
    }

    [Test]
    public void RequestAsync_FailsHashRejected_WhenPeerSendsReject()
    {
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);

        coord.HandleReject(new Bep52WireMessages.HashReject(root, 0, 0, 2, 1));

        Assert.ThrowsAsync<HashRejectedException>(async () => await reqTask);
        Assert.That(coord.OutstandingCount, Is.EqualTo(0));
    }

    [Test]
    public void RequestAsync_FailsTimeout_WhenNoResponse()
    {
        var coord = new V2HashRequestCoordinator { DefaultTimeout = TimeSpan.FromMilliseconds(50) };
        var root = new byte[32];
        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);

        Assert.ThrowsAsync<TimeoutException>(async () => await reqTask);
        Assert.That(coord.OutstandingCount, Is.EqualTo(0));
    }

    [Test]
    public void RequestAsync_FailsInvalidOperation_WhenPeerReturnsBadHashes()
    {
        // Peer returns hashes that don't verify against the claimed pieces_root (wrong
        // hash count OR cryptographically inconsistent). MerkleProofVerifier returns false
        // and the coordinator surfaces that as a failure so the caller can re-request
        // against a different peer.
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32]; // all zeros - won't match any valid Merkle path
        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);

        // Fabricate a "hashes" response with 3 hashes (length 2 + proof_layers 1) but the
        // computed root won't match the claimed all-zeros root.
        var fakeHashes = new[]
        {
            SHA256.HashData(new byte[] { 1 }),
            SHA256.HashData(new byte[] { 2 }),
            SHA256.HashData(new byte[] { 3 }),
        };
        coord.HandleHashes(new Bep52WireMessages.Hashes(root, 0, 0, 2, 1, fakeHashes));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await reqTask);
        Assert.That(coord.OutstandingCount, Is.EqualTo(0));
    }

    [Test]
    public void RequestAsync_Cancellation_DisposesCorrelation()
    {
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        using var cts = new CancellationTokenSource();

        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: _ => Task.CompletedTask,
            ct: cts.Token);

        cts.Cancel();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await reqTask);
        Assert.That(coord.OutstandingCount, Is.EqualTo(0));
    }

    [Test]
    public void RequestAsync_DuplicateKey_Throws()
    {
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        var req = new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1);

        _ = coord.RequestAsync(req, send: _ => Task.CompletedTask);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coord.RequestAsync(req, send: _ => Task.CompletedTask));

        // First request still outstanding - count = 1, not 2.
        Assert.That(coord.OutstandingCount, Is.EqualTo(1));
    }

    [Test]
    public void HandleHashes_Unsolicited_DroppedSilently()
    {
        // No outstanding request - a hashes message for an unknown key is just dropped.
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        var hashes = new[] { SHA256.HashData(new byte[] { 1 }) };

        Assert.DoesNotThrow(() =>
            coord.HandleHashes(new Bep52WireMessages.Hashes(root, 0, 0, 1, 0, hashes)));
        Assert.That(coord.OutstandingCount, Is.EqualTo(0));
    }

    [Test]
    public void HandleReject_Unsolicited_DroppedSilently()
    {
        var coord = new V2HashRequestCoordinator();
        Assert.DoesNotThrow(() =>
            coord.HandleReject(new Bep52WireMessages.HashReject(new byte[32], 0, 0, 1, 0)));
    }

    [Test]
    public void SendCallback_Throws_RequestEntryRemoved()
    {
        // If the send callback fails (e.g. wire closed), the pending entry must be cleaned
        // up so a later retry with the same key is allowed.
        var coord = new V2HashRequestCoordinator();
        var req = new Bep52WireMessages.HashRequest(new byte[32], 0, 0, 2, 1);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coord.RequestAsync(req, send: _ => throw new InvalidOperationException("wire closed")));

        Assert.That(coord.OutstandingCount, Is.EqualTo(0),
            "Failed send must not leak a correlation entry.");

        // A second request with the same key must now succeed (not throw duplicate-key).
        var retry = coord.RequestAsync(req, send: _ => Task.CompletedTask);
        Assert.That(coord.OutstandingCount, Is.EqualTo(1));
        _ = retry; // don't await - test only verifies entry was cleanly registrable
    }

    [Test]
    public async Task TwoSimultaneousRequests_DifferentKeys_BothResolve()
    {
        // Two outstanding requests with different keys can be in flight at once and
        // receive their own responses independently.
        var leaves = Enumerable.Range(0, 4).Select(i => SHA256.HashData(new byte[] { (byte)i })).ToArray();
        var rootA = HashPair(HashPair(leaves[0], leaves[1]), HashPair(leaves[2], leaves[3]));
        var rootB = HashPair(HashPair(leaves[2], leaves[3]), HashPair(leaves[0], leaves[1])); // different order -> different root
        var h23A = HashPair(leaves[2], leaves[3]);
        var h01B = HashPair(leaves[0], leaves[1]);

        var coord = new V2HashRequestCoordinator();
        var taskA = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(rootA, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);
        var taskB = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(rootB, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);

        Assert.That(coord.OutstandingCount, Is.EqualTo(2));

        // Respond to B first, then A - ordering independent.
        coord.HandleHashes(new Bep52WireMessages.Hashes(rootB, 0, 0, 2, 1,
            new[] { leaves[2], leaves[3], h01B }));
        coord.HandleHashes(new Bep52WireMessages.Hashes(rootA, 0, 0, 2, 1,
            new[] { leaves[0], leaves[1], h23A }));

        var resB = await taskB;
        var resA = await taskA;
        Assert.That(resA.Length, Is.EqualTo(3));
        Assert.That(resB.Length, Is.EqualTo(3));
        Assert.That(coord.OutstandingCount, Is.EqualTo(0));
    }
}
