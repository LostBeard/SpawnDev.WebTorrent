using System.Security.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 V2HashRequestCoordinator state-machine tests: happy path, reject path,
/// timeout, verification failure, cancellation, unsolicited-response drop, duplicate-key
/// rejection, send-callback failure cleanup. Migrated from NUnit so they run under
/// PlaywrightMultiTest (browser + desktop).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task V2HashCoord_RequestAsync_ReturnsHashes_WhenPeerRespondsWithVerifiedData()
    {
        var leaves = Enumerable.Range(0, 4).Select(i => SHA256.HashData(new byte[] { (byte)i })).ToArray();
        var h01 = V2HashCoordTests_HashPair(leaves[0], leaves[1]);
        var h23 = V2HashCoordTests_HashPair(leaves[2], leaves[3]);
        var root = V2HashCoordTests_HashPair(h01, h23);

        var coord = new V2HashRequestCoordinator();
        Bep52WireMessages.HashRequest? sentReq = null;
        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: r => { sentReq = r; return Task.CompletedTask; });

        if (sentReq is null) throw new Exception("send callback must run synchronously inside RequestAsync");

        coord.HandleHashes(new Bep52WireMessages.Hashes(
            root, 0, 0, 2, 1,
            new[] { leaves[0], leaves[1], h23 }));

        var result = await reqTask;
        if (result.Length != 3) throw new Exception($"result.Length={result.Length}, expected 3");
        if (coord.OutstandingCount != 0) throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 0");
    }

    [TestMethod]
    public async Task V2HashCoord_RequestAsync_FailsHashRejected_WhenPeerSendsReject()
    {
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);

        coord.HandleReject(new Bep52WireMessages.HashReject(root, 0, 0, 2, 1));

        try { await reqTask; }
        catch (HashRejectedException)
        {
            if (coord.OutstandingCount != 0) throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 0 after reject");
            return;
        }
        throw new Exception("expected HashRejectedException");
    }

    [TestMethod]
    public async Task V2HashCoord_RequestAsync_FailsTimeout_WhenNoResponse()
    {
        var coord = new V2HashRequestCoordinator { DefaultTimeout = TimeSpan.FromMilliseconds(50) };
        var root = new byte[32];
        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);

        try { await reqTask; }
        catch (TimeoutException)
        {
            if (coord.OutstandingCount != 0) throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 0 after timeout");
            return;
        }
        throw new Exception("expected TimeoutException");
    }

    [TestMethod]
    public async Task V2HashCoord_RequestAsync_FailsInvalidOperation_WhenPeerReturnsBadHashes()
    {
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);

        var fakeHashes = new[]
        {
            SHA256.HashData(new byte[] { 1 }),
            SHA256.HashData(new byte[] { 2 }),
            SHA256.HashData(new byte[] { 3 }),
        };
        coord.HandleHashes(new Bep52WireMessages.Hashes(root, 0, 0, 2, 1, fakeHashes));

        try { await reqTask; }
        catch (InvalidOperationException)
        {
            if (coord.OutstandingCount != 0) throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 0");
            return;
        }
        throw new Exception("expected InvalidOperationException on unverifiable hashes");
    }

    [TestMethod]
    public async Task V2HashCoord_RequestAsync_Cancellation_DisposesCorrelation()
    {
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        using var cts = new CancellationTokenSource();

        var reqTask = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1),
            send: _ => Task.CompletedTask,
            ct: cts.Token);

        cts.Cancel();

        try { await reqTask; }
        catch (TaskCanceledException)
        {
            if (coord.OutstandingCount != 0) throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 0 after cancel");
            return;
        }
        throw new Exception("expected TaskCanceledException");
    }

    [TestMethod]
    public async Task V2HashCoord_RequestAsync_DuplicateKey_Throws()
    {
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        var req = new Bep52WireMessages.HashRequest(root, 0, 0, 2, 1);

        _ = coord.RequestAsync(req, send: _ => Task.CompletedTask);

        try { await coord.RequestAsync(req, send: _ => Task.CompletedTask); }
        catch (InvalidOperationException)
        {
            if (coord.OutstandingCount != 1)
                throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 1 (first still in flight)");
            return;
        }
        throw new Exception("expected InvalidOperationException for duplicate key");
    }

    [TestMethod]
    public async Task V2HashCoord_HandleHashes_Unsolicited_DroppedSilently()
    {
        var coord = new V2HashRequestCoordinator();
        var root = new byte[32];
        var hashes = new[] { SHA256.HashData(new byte[] { 1 }) };

        coord.HandleHashes(new Bep52WireMessages.Hashes(root, 0, 0, 1, 0, hashes));
        if (coord.OutstandingCount != 0) throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 0");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task V2HashCoord_HandleReject_Unsolicited_DroppedSilently()
    {
        var coord = new V2HashRequestCoordinator();
        coord.HandleReject(new Bep52WireMessages.HashReject(new byte[32], 0, 0, 1, 0));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task V2HashCoord_SendCallback_Throws_RequestEntryRemoved()
    {
        var coord = new V2HashRequestCoordinator();
        var req = new Bep52WireMessages.HashRequest(new byte[32], 0, 0, 2, 1);

        try { await coord.RequestAsync(req, send: _ => throw new InvalidOperationException("wire closed")); }
        catch (InvalidOperationException) { /* expected */ }

        if (coord.OutstandingCount != 0)
            throw new Exception($"OutstandingCount={coord.OutstandingCount} after failed send, expected 0 (entry must be cleaned up)");

        var retry = coord.RequestAsync(req, send: _ => Task.CompletedTask);
        if (coord.OutstandingCount != 1)
            throw new Exception($"OutstandingCount={coord.OutstandingCount} after retry, expected 1");
        _ = retry;
    }

    [TestMethod]
    public async Task V2HashCoord_TwoSimultaneousRequests_DifferentKeys_BothResolve()
    {
        var leaves = Enumerable.Range(0, 4).Select(i => SHA256.HashData(new byte[] { (byte)i })).ToArray();
        var rootA = V2HashCoordTests_HashPair(
            V2HashCoordTests_HashPair(leaves[0], leaves[1]), V2HashCoordTests_HashPair(leaves[2], leaves[3]));
        var rootB = V2HashCoordTests_HashPair(
            V2HashCoordTests_HashPair(leaves[2], leaves[3]), V2HashCoordTests_HashPair(leaves[0], leaves[1]));
        var h23A = V2HashCoordTests_HashPair(leaves[2], leaves[3]);
        var h01B = V2HashCoordTests_HashPair(leaves[0], leaves[1]);

        var coord = new V2HashRequestCoordinator();
        var taskA = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(rootA, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);
        var taskB = coord.RequestAsync(
            new Bep52WireMessages.HashRequest(rootB, 0, 0, 2, 1),
            send: _ => Task.CompletedTask);

        if (coord.OutstandingCount != 2) throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 2");

        coord.HandleHashes(new Bep52WireMessages.Hashes(rootB, 0, 0, 2, 1,
            new[] { leaves[2], leaves[3], h01B }));
        coord.HandleHashes(new Bep52WireMessages.Hashes(rootA, 0, 0, 2, 1,
            new[] { leaves[0], leaves[1], h23A }));

        var resB = await taskB;
        var resA = await taskA;
        if (resA.Length != 3) throw new Exception($"resA.Length={resA.Length}, expected 3");
        if (resB.Length != 3) throw new Exception($"resB.Length={resB.Length}, expected 3");
        if (coord.OutstandingCount != 0) throw new Exception($"OutstandingCount={coord.OutstandingCount}, expected 0");
    }

    // ---- helpers ----

    private static byte[] V2HashCoordTests_HashPair(byte[] l, byte[] r)
    {
        var b = new byte[64];
        l.CopyTo(b, 0);
        r.CopyTo(b, 32);
        return SHA256.HashData(b);
    }
}
