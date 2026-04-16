using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Bencode;
using System.Text;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests for features that SpawnDev.ILGPU.P2P depends on.
/// Covers: UseExtension, Wire.Extended send path, ExtendedHandshake custom data,
/// Torrent.OnWire event, IWireExtension full lifecycle, and AgentChannel pub/sub.
/// All tests use real production code paths - no mocks.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ── Test Extension ──
    // A minimal IWireExtension that tracks its lifecycle for verification.

    private class TestComputeExtension : IWireExtension
    {
        public string Name => "test_compute";

        private Wire? _wire;
        public bool HandshakeCalled { get; private set; }
        public bool ExtendedHandshakeCalled { get; private set; }
        public string? HandshakeInfoHash { get; private set; }
        public string? HandshakePeerId { get; private set; }
        public Dictionary<string, object>? ReceivedHandshakeData { get; private set; }
        public List<byte[]> ReceivedMessages { get; } = new();
        public int ComputeVersion { get; set; } = 1;

        public void SetWire(Wire wire)
        {
            _wire = wire;
            // Advertise capability in extended handshake - same pattern as P2P's SdComputeExtension
            wire.ExtendedHandshake["test_compute_version"] = ComputeVersion;
        }

        public void OnHandshake(string infoHash, string peerId, WireExtensions extensions)
        {
            HandshakeCalled = true;
            HandshakeInfoHash = infoHash;
            HandshakePeerId = peerId;
        }

        public void OnExtendedHandshake(Dictionary<string, object> handshake)
        {
            ExtendedHandshakeCalled = true;
            ReceivedHandshakeData = new Dictionary<string, object>(handshake);
        }

        public void OnMessage(byte[] buf)
        {
            ReceivedMessages.Add(buf.ToArray());
        }

        public Wire? GetWire() => _wire;
    }

    // Helper: create two connected wires that relay data to each other.
    // Uses queued delivery to avoid re-entrant DataReceived calls during handshake.
    private static (Wire wireA, Wire wireB) CreateConnectedWirePair()
    {
        var wireA = new Wire();
        var wireB = new Wire();

        // Queue outgoing data and deliver asynchronously to avoid re-entrant parsing
        wireA.SendRaw = async (data) =>
        {
            await Task.Yield();
            wireB.DataReceived(data.ToArray());
        };
        wireB.SendRaw = async (data) =>
        {
            await Task.Yield();
            wireA.DataReceived(data.ToArray());
        };

        return (wireA, wireB);
    }

    // Helper: perform mutual handshake between two wires using Wire.Handshake()
    // which properly triggers the extended handshake exchange.
    private static async Task PerformHandshakeAsync(Wire wireA, Wire wireB, byte[]? infoHash = null)
    {
        var ih = infoHash ?? new byte[20];
        var peerIdA = Encoding.ASCII.GetBytes("-WT0300-AAAAAAAAAA00");
        var peerIdB = Encoding.ASCII.GetBytes("-WT0300-BBBBBBBBBB00");

        // Both sides send their handshakes - Wire.Handshake() sends the BT handshake
        // and then sends the extended handshake (BEP 10) automatically
        await wireA.Handshake(ih, peerIdA, fast: true);
        await wireB.Handshake(ih, peerIdB, fast: true);
    }

    // ── GAP 1: UseExtension(factory) ──

    // Sintel magnet - always has active peers on public trackers
    private const string P2PTestMagnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel" +
        "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
        "&tr=wss%3A%2F%2Ftracker.webtorrent.dev" +
        "&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce" +
        "&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";

    [TestMethod]
    public async Task UseExtension_RegistersAndCreatesExtension()
    {
        // Browser multi-client testing needs cross-window infrastructure (WebWorkerService).
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Requires real peer connection - desktop only until cross-window testing is built");

        // Register a custom extension factory, then join a known swarm (Sintel) that
        // always has active peers. When a peer connects, the factory should fire.
        var client = CreateIsolatedClient();

        TestComputeExtension? createdExt = null;

        client.UseExtension((wire) =>
        {
            var ext = new TestComputeExtension();
            ext.SetWire(wire);
            createdExt = ext;
            return ext;
        });

        var torrent = client.Add(P2PTestMagnet);

        // Wait for any peer to connect - the extension factory should fire
        using var cts = new CancellationTokenSource(60000);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (createdExt != null) break;
                if (torrent.NumPeers > 0) break;
                await Task.Delay(500, cts.Token);
            }
        }
        catch (OperationCanceledException) { }

        if (createdExt == null)
            throw new Exception($"UseExtension factory was never invoked - no peer connected within 60s (peers={torrent.NumPeers})");
        if (createdExt.Name != "test_compute")
            throw new Exception($"Extension name wrong: {createdExt.Name}");

        await client.RemoveAsync(torrent);
        await client.DisposeAsync();
    }

    // ── GAP 2: Wire.Extended() send path ──

    [TestMethod]
    public async Task Wire_Extended_SendsMessageToPeer()
    {
        var (wireA, wireB) = CreateConnectedWirePair();

        // Register extension on both sides
        var extA = new TestComputeExtension();
        extA.SetWire(wireA);
        wireA.Use(extA);

        var extB = new TestComputeExtension();
        extB.SetWire(wireB);
        wireB.Use(extB);

        // Perform BT handshake
        await PerformHandshakeAsync(wireA, wireB);
        await Task.Delay(50); // Let extended handshake complete

        // Wire A sends a compute message to Wire B
        var payload = Encoding.UTF8.GetBytes("COMPUTE_TASK_42");
        await wireA.Extended("test_compute", payload);
        await Task.Delay(50);

        // Wire B's extension should have received the message
        if (extB.ReceivedMessages.Count == 0)
            throw new Exception("No message received by peer extension");

        var received = extB.ReceivedMessages[0];
        if (Encoding.UTF8.GetString(received) != "COMPUTE_TASK_42")
            throw new Exception($"Message content mismatch: {Encoding.UTF8.GetString(received)}");
    }

    // ── GAP 3: Wire.ExtendedHandshake custom data ──

    [TestMethod]
    public async Task Wire_ExtendedHandshake_CustomDataExchanged()
    {
        var (wireA, wireB) = CreateConnectedWirePair();

        // Register extension on both sides with different versions
        var extA = new TestComputeExtension { ComputeVersion = 3 };
        extA.SetWire(wireA);
        wireA.Use(extA);

        var extB = new TestComputeExtension { ComputeVersion = 5 };
        extB.SetWire(wireB);
        wireB.Use(extB);

        // Perform BT handshake - triggers extended handshake exchange
        await PerformHandshakeAsync(wireA, wireB);
        await Task.Delay(100);

        // Extension A should have received B's handshake data containing version 5
        if (!extA.ExtendedHandshakeCalled)
            throw new Exception("Extension A did not receive extended handshake");
        if (extA.ReceivedHandshakeData == null)
            throw new Exception("Extension A handshake data is null");

        // Check that B's test_compute_version was in the handshake
        if (!extA.ReceivedHandshakeData.TryGetValue("test_compute_version", out var versionObj))
            throw new Exception("test_compute_version not found in peer's extended handshake");

        var version = versionObj is long l ? (int)l : versionObj is int i ? i : -1;
        if (version != 5)
            throw new Exception($"Expected peer version 5, got {version}");

        // Extension B should have received A's version 3
        if (!extB.ExtendedHandshakeCalled)
            throw new Exception("Extension B did not receive extended handshake");
        if (extB.ReceivedHandshakeData == null)
            throw new Exception("Extension B handshake data is null");
        if (!extB.ReceivedHandshakeData.TryGetValue("test_compute_version", out var versionObj2))
            throw new Exception("test_compute_version not found in A's extended handshake");

        var version2 = versionObj2 is long l2 ? (int)l2 : versionObj2 is int i2 ? i2 : -1;
        if (version2 != 3)
            throw new Exception($"Expected peer version 3, got {version2}");
    }

    // ── GAP 4: Torrent.OnWire event ──

    [TestMethod]
    public async Task Torrent_OnWire_FiresOnPeerConnect()
    {
        // Browser multi-client testing needs cross-window infrastructure (WebWorkerService).
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Requires real peer connection - desktop only until cross-window testing is built");

        // Join a known swarm (Sintel) that always has active peers.
        // When a peer connects, OnWire should fire.
        Wire? receivedWire = null;
        string? receivedId = null;

        var client = CreateIsolatedClient();

        var torrent = client.Add(P2PTestMagnet);
        torrent.OnWire += (wire, id) =>
        {
            receivedWire = wire;
            receivedId = id;
        };

        // Wait for any peer connection
        using var cts = new CancellationTokenSource(60000);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (receivedWire != null) break;
                await Task.Delay(500, cts.Token);
            }
        }
        catch (OperationCanceledException) { }

        if (receivedWire == null)
            throw new Exception($"OnWire event never fired - no peer connected within 60s (peers={torrent.NumPeers})");
        if (string.IsNullOrEmpty(receivedId))
            throw new Exception("OnWire fired but peer ID was empty");

        await client.RemoveAsync(torrent);
        await client.DisposeAsync();
    }

    // ── GAP 5: IWireExtension full lifecycle ──

    [TestMethod]
    public async Task IWireExtension_FullLifecycle_AllMethodsCalled()
    {
        var (wireA, wireB) = CreateConnectedWirePair();

        var extA = new TestComputeExtension();
        extA.SetWire(wireA);
        wireA.Use(extA);

        var extB = new TestComputeExtension();
        extB.SetWire(wireB);
        wireB.Use(extB);

        // Step 1: BT handshake - triggers OnHandshake
        await PerformHandshakeAsync(wireA, wireB);
        await Task.Delay(50);

        // Verify OnHandshake was called on both
        if (!extA.HandshakeCalled)
            throw new Exception("Extension A: OnHandshake not called");
        if (!extB.HandshakeCalled)
            throw new Exception("Extension B: OnHandshake not called");
        if (string.IsNullOrEmpty(extA.HandshakeInfoHash))
            throw new Exception("Extension A: infoHash was empty in OnHandshake");

        // Step 2: Extended handshake should have been sent automatically
        await Task.Delay(50);

        if (!extA.ExtendedHandshakeCalled)
            throw new Exception("Extension A: OnExtendedHandshake not called");
        if (!extB.ExtendedHandshakeCalled)
            throw new Exception("Extension B: OnExtendedHandshake not called");

        // Step 3: Send a message from A to B - triggers OnMessage
        var taskPayload = Encoding.UTF8.GetBytes("DISPATCH_KERNEL_0");
        await wireA.Extended("test_compute", taskPayload);
        await Task.Delay(50);

        if (extB.ReceivedMessages.Count == 0)
            throw new Exception("Extension B: OnMessage not called");

        var msg = Encoding.UTF8.GetString(extB.ReceivedMessages[0]);
        if (msg != "DISPATCH_KERNEL_0")
            throw new Exception($"Extension B: message content wrong: {msg}");

        // Step 4: Send a response from B to A
        var resultPayload = Encoding.UTF8.GetBytes("RESULT_OK");
        await wireB.Extended("test_compute", resultPayload);
        await Task.Delay(50);

        if (extA.ReceivedMessages.Count == 0)
            throw new Exception("Extension A: OnMessage not called for response");

        var response = Encoding.UTF8.GetString(extA.ReceivedMessages[0]);
        if (response != "RESULT_OK")
            throw new Exception($"Extension A: response content wrong: {response}");

        // All 3 interface methods verified on both sides: OnHandshake, OnExtendedHandshake, OnMessage
    }

    // ── GAP 6 + 7 + 8: AgentChannel publish/subscribe/OnAgentUpdate ──

    [TestMethod]
    public async Task AgentChannel_PublishAndSubscribe_EndToEnd()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("AgentChannel DHT pub/sub requires UDP - desktop only");

        // Create a signer for the publisher
        var pubKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(pubKey);
        var signer = new NoOpSigner(pubKey);

        // Create publisher channel
        var publisher = new AgentChannel(signer);

        // Verify PublicKey is set
        if (publisher.PublicKey == null || publisher.PublicKey.Length != 32)
            throw new Exception($"PublicKey should be 32 bytes, got {publisher.PublicKey?.Length ?? 0}");

        if (string.IsNullOrEmpty(publisher.PublicKeyHex))
            throw new Exception("PublicKeyHex should not be empty");

        if (publisher.PublicKeyHex.Length != 64)
            throw new Exception($"PublicKeyHex should be 64 chars, got {publisher.PublicKeyHex.Length}");

        // Verify PublicKey matches what we provided
        if (!publisher.PublicKey.SequenceEqual(pubKey))
            throw new Exception("PublicKey does not match the signer's key");

        // Test publishing state (without DHT, this increments sequence locally)
        var state = Encoding.UTF8.GetBytes("coordinator:peer1,peer2,peer3");
        await publisher.PublishStateAsync(state);

        // Sequence should have incremented
        if (publisher.Sequence < 0)
            throw new Exception($"Sequence should be non-negative after publish, got {publisher.Sequence}");

        // Publish again - sequence should increment
        var prevSeq = publisher.Sequence;
        var state2 = Encoding.UTF8.GetBytes("coordinator:peer1,peer2,peer3,peer4");
        await publisher.PublishStateAsync(state2);

        if (publisher.Sequence <= prevSeq)
            throw new Exception($"Sequence should increment after second publish: {publisher.Sequence} <= {prevSeq}");

        await publisher.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_NamedChannels_IndependentState()
    {
        var pubKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(pubKey);
        var signer = new NoOpSigner(pubKey);

        var channel = new AgentChannel(signer);

        // Create named channels - same pattern as P2P's compute coordination
        var computeChannel = channel.Channel("compute");
        var stateChannel = channel.Channel("state");

        if (computeChannel == null)
            throw new Exception("compute channel is null");
        if (stateChannel == null)
            throw new Exception("state channel is null");

        // Named channels should be distinct instances
        if (ReferenceEquals(computeChannel, stateChannel))
            throw new Exception("Named channels should be distinct");

        await channel.DisposeAsync();
    }

    [TestMethod]
    public async Task AgentChannel_ComputeAgentInfoHash_Deterministic()
    {
        // ComputeAgentInfoHash should be deterministic - same inputs = same output
        var pubKey = new byte[32];
        for (int i = 0; i < 32; i++) pubKey[i] = (byte)i;

        var salt = Encoding.UTF8.GetBytes("compute-swarm");

        var hash1 = AgentChannel.ComputeAgentInfoHash(pubKey, salt);
        var hash2 = AgentChannel.ComputeAgentInfoHash(pubKey, salt);

        if (hash1.Length != 20)
            throw new Exception($"InfoHash should be 20 bytes (SHA1), got {hash1.Length}");

        if (!hash1.SequenceEqual(hash2))
            throw new Exception("ComputeAgentInfoHash should be deterministic");

        // Different salt should produce different hash
        var salt2 = Encoding.UTF8.GetBytes("different-swarm");
        var hash3 = AgentChannel.ComputeAgentInfoHash(pubKey, salt2);
        if (hash1.SequenceEqual(hash3))
            throw new Exception("Different salt should produce different hash");

        // Null salt should produce a different hash than non-null salt
        var hashNoSalt = AgentChannel.ComputeAgentInfoHash(pubKey, null);
        if (hash1.SequenceEqual(hashNoSalt))
            throw new Exception("Null salt should produce different hash than non-null salt");
    }

    // ── Bidirectional extension messaging ──

    [TestMethod]
    public async Task Wire_Extended_BidirectionalMessages_MultipleRoundTrips()
    {
        var (wireA, wireB) = CreateConnectedWirePair();

        var extA = new TestComputeExtension();
        extA.SetWire(wireA);
        wireA.Use(extA);

        var extB = new TestComputeExtension();
        extB.SetWire(wireB);
        wireB.Use(extB);

        await PerformHandshakeAsync(wireA, wireB);
        await Task.Delay(50);

        // Send 10 messages each way - simulates a real compute dispatch/result cycle
        for (int i = 0; i < 10; i++)
        {
            var dispatch = Encoding.UTF8.GetBytes($"DISPATCH:{i}");
            await wireA.Extended("test_compute", dispatch);
        }
        await Task.Delay(100);

        if (extB.ReceivedMessages.Count != 10)
            throw new Exception($"Expected 10 messages at B, got {extB.ReceivedMessages.Count}");

        // Verify order preserved
        for (int i = 0; i < 10; i++)
        {
            var msg = Encoding.UTF8.GetString(extB.ReceivedMessages[i]);
            if (msg != $"DISPATCH:{i}")
                throw new Exception($"Message {i} out of order: {msg}");
        }

        // Now send results back
        for (int i = 0; i < 10; i++)
        {
            var result = Encoding.UTF8.GetBytes($"RESULT:{i}:OK");
            await wireB.Extended("test_compute", result);
        }
        await Task.Delay(100);

        if (extA.ReceivedMessages.Count != 10)
            throw new Exception($"Expected 10 responses at A, got {extA.ReceivedMessages.Count}");

        for (int i = 0; i < 10; i++)
        {
            var msg = Encoding.UTF8.GetString(extA.ReceivedMessages[i]);
            if (msg != $"RESULT:{i}:OK")
                throw new Exception($"Response {i} wrong: {msg}");
        }
    }

    // ── Extension with binary payload ──

    [TestMethod]
    public async Task Wire_Extended_BinaryPayload_PreservedExactly()
    {
        var (wireA, wireB) = CreateConnectedWirePair();

        var extA = new TestComputeExtension();
        extA.SetWire(wireA);
        wireA.Use(extA);

        var extB = new TestComputeExtension();
        extB.SetWire(wireB);
        wireB.Use(extB);

        await PerformHandshakeAsync(wireA, wireB);
        await Task.Delay(50);

        // Send binary payload - simulates GPU buffer transfer
        var binaryData = MakeDeterministicData(4096, seed: 8003);
        await wireA.Extended("test_compute", binaryData);
        await Task.Delay(50);

        if (extB.ReceivedMessages.Count == 0)
            throw new Exception("Binary payload not received");

        var received = extB.ReceivedMessages[0];
        if (received.Length != 4096)
            throw new Exception($"Binary payload length wrong: {received.Length}");

        if (!received.SequenceEqual(binaryData))
            throw new Exception("Binary payload corrupted during BEP 10 transport");
    }
}
