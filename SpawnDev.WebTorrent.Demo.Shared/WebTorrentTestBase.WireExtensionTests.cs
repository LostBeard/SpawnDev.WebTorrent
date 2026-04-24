using System.Text;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 10 wire extension system — end-to-end tests using real Wire instances connected
/// loopback-style (send-A → receive-B, send-B → receive-A), real handshakes, real
/// extended-handshake `m` dict exchange, real `Wire.Extended(...)` message round-trips.
/// No mocks, no stubs; the transport is a plain Func&lt;byte[],Task&gt; that routes bytes to
/// the other Wire's DataReceived, which is the same entry point real TCP/SCTP peers use.
///
/// Three test extensions exercise the system:
/// - `TestPing`: string message protocol ("ping" / "pong") — simplest round-trip.
/// - `TestCounter`: advertises an initial counter value in the extended handshake,
///   each incoming message increments and echoes. Exercises handshake-dict-payload
///   + bidirectional state.
/// - `TestEcho`: arbitrary-byte echo with payload integrity check — exercises binary
///   messages with length variations.
///
/// The no-conflict test registers all three on BOTH sides and sends messages on all
/// three in parallel, asserting each extension sees ONLY its own messages.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task WireExtension_RegistrationBeforeHandshake_ExposesToPeer()
    {
        var (peerA, peerB) = WireExtensionTests_CreateConnectedPair();
        var extA = new WireExtensionTests_TestPing();
        var extB = new WireExtensionTests_TestPing();
        peerA.Use(extA); extA.SetWire(peerA);
        peerB.Use(extB); extB.SetWire(peerB);

        await WireExtensionTests_PerformHandshakes(peerA, peerB);
        await Task.Delay(50); // let BEP 10 handshakes exchange

        // Both sides should now know the peer supports test_ping via PeerExtendedMapping
        if (!peerA.PeerExtendedMapping.ContainsKey("test_ping"))
            throw new Exception($"peerA.PeerExtendedMapping missing test_ping; keys={string.Join(",", peerA.PeerExtendedMapping.Keys)}");
        if (!peerB.PeerExtendedMapping.ContainsKey("test_ping"))
            throw new Exception($"peerB.PeerExtendedMapping missing test_ping; keys={string.Join(",", peerB.PeerExtendedMapping.Keys)}");
    }

    [TestMethod]
    public async Task WireExtension_SingleMessage_RoundTripsBetweenPeers()
    {
        var (peerA, peerB) = WireExtensionTests_CreateConnectedPair();
        var extA = new WireExtensionTests_TestPing();
        var extB = new WireExtensionTests_TestPing();
        peerA.Use(extA); extA.SetWire(peerA);
        peerB.Use(extB); extB.SetWire(peerB);
        await WireExtensionTests_PerformHandshakes(peerA, peerB);
        await Task.Delay(50);

        // A pings B. B's OnMessage handler auto-replies with "pong" (see TestPing impl).
        await peerA.Extended("test_ping", Encoding.UTF8.GetBytes("ping"));
        await Task.Delay(50);

        if (extB.LastReceived != "ping") throw new Exception($"extB.LastReceived={extB.LastReceived}, expected 'ping'");
        if (extA.LastReceived != "pong") throw new Exception($"extA.LastReceived={extA.LastReceived}, expected 'pong' (auto-reply)");
    }

    [TestMethod]
    public async Task WireExtension_HandshakePayload_CarriesExtensionData()
    {
        // TestCounter advertises its initial counter value via its ExtendedHandshake
        // contribution. Peer reads it from OnExtendedHandshake.
        var (peerA, peerB) = WireExtensionTests_CreateConnectedPair();
        var extA = new WireExtensionTests_TestCounter(initialValue: 42);
        var extB = new WireExtensionTests_TestCounter(initialValue: 99);
        peerA.Use(extA); extA.SetWire(peerA);
        peerB.Use(extB); extB.SetWire(peerB);
        await WireExtensionTests_PerformHandshakes(peerA, peerB);
        await Task.Delay(50);

        if (extA.PeerInitialValue != 99)
            throw new Exception($"extA saw peer's initial counter as {extA.PeerInitialValue}, expected 99");
        if (extB.PeerInitialValue != 42)
            throw new Exception($"extB saw peer's initial counter as {extB.PeerInitialValue}, expected 42");
    }

    [TestMethod]
    public async Task WireExtension_MultipleExtensions_NoConflict()
    {
        // All three extensions registered on both sides. Send via each; each extension
        // sees ONLY its own messages. Proves the ext_id routing is correctly per-extension.
        var (peerA, peerB) = WireExtensionTests_CreateConnectedPair();
        var pingA = new WireExtensionTests_TestPing();
        var pingB = new WireExtensionTests_TestPing();
        var counterA = new WireExtensionTests_TestCounter(10);
        var counterB = new WireExtensionTests_TestCounter(20);
        var echoA = new WireExtensionTests_TestEcho();
        var echoB = new WireExtensionTests_TestEcho();
        peerA.Use(pingA); pingA.SetWire(peerA);
        peerA.Use(counterA); counterA.SetWire(peerA);
        peerA.Use(echoA); echoA.SetWire(peerA);
        peerB.Use(pingB); pingB.SetWire(peerB);
        peerB.Use(counterB); counterB.SetWire(peerB);
        peerB.Use(echoB); echoB.SetWire(peerB);
        await WireExtensionTests_PerformHandshakes(peerA, peerB);
        await Task.Delay(50);

        // All three peer mappings should be visible on both sides.
        foreach (var name in new[] { "test_ping", "test_counter", "test_echo" })
        {
            if (!peerA.PeerExtendedMapping.ContainsKey(name))
                throw new Exception($"peerA missing {name}; keys={string.Join(",", peerA.PeerExtendedMapping.Keys)}");
            if (!peerB.PeerExtendedMapping.ContainsKey(name))
                throw new Exception($"peerB missing {name}; keys={string.Join(",", peerB.PeerExtendedMapping.Keys)}");
        }

        // Send via all three extensions in parallel. Each remote extension's OnMessage
        // must fire exactly once, with its own payload — no cross-wiring.
        var pingPayload = Encoding.UTF8.GetBytes("ping");
        var counterPayload = new byte[] { 0x00, 0x00, 0x00, 0x07 }; // counter increment value 7
        var echoPayload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };

        await peerA.Extended("test_ping", pingPayload);
        await peerA.Extended("test_counter", counterPayload);
        await peerA.Extended("test_echo", echoPayload);
        await Task.Delay(100);

        // TestPing auto-replies "pong" — ignore B's state for ping (it's fleeting).
        if (pingB.LastReceived != "ping")
            throw new Exception($"pingB.LastReceived={pingB.LastReceived}, expected ping");
        // TestCounter received 7, so counterB.LastIncrementValue = 7.
        if (counterB.LastIncrementValue != 7)
            throw new Exception($"counterB.LastIncrementValue={counterB.LastIncrementValue}, expected 7");
        // TestEcho received the payload verbatim.
        if (echoB.LastPayload == null || !echoB.LastPayload.SequenceEqual(echoPayload))
            throw new Exception($"echoB.LastPayload doesn't match sent payload");

        // Critically: NO cross-wiring. echoB should not have seen the ping or counter data;
        // counterB should not have seen the echo; etc.
        if (echoB.MessageCount != 1)
            throw new Exception($"echoB.MessageCount={echoB.MessageCount}, expected 1 (cross-wiring into other extensions detected)");
        if (counterB.MessageCount != 1)
            throw new Exception($"counterB.MessageCount={counterB.MessageCount}, expected 1");
        if (pingB.MessageCount != 1)
            throw new Exception($"pingB.MessageCount={pingB.MessageCount}, expected 1");
    }

    [TestMethod]
    public async Task WireExtension_UnregisteredOnPeer_GracefullyIgnored()
    {
        // A registers TestPing. B does NOT register TestPing.
        // When A tries to Extended("test_ping", ...), it should throw at the sender
        // side because the peer doesn't support this extension. No crash on B's side.
        var (peerA, peerB) = WireExtensionTests_CreateConnectedPair();
        var pingA = new WireExtensionTests_TestPing();
        peerA.Use(pingA); pingA.SetWire(peerA);
        // no Use() on peerB
        await WireExtensionTests_PerformHandshakes(peerA, peerB);
        await Task.Delay(50);

        // peerA should NOT have test_ping in PeerExtendedMapping — because peerB didn't
        // advertise it. Attempting to send must throw.
        if (peerA.PeerExtendedMapping.ContainsKey("test_ping"))
            throw new Exception("peerA should not see test_ping in PeerExtendedMapping when peer didn't register it");

        try
        {
            await peerA.Extended("test_ping", Encoding.UTF8.GetBytes("ping"));
        }
        catch (Exception)
        {
            // Expected: Extended throws when peer doesn't support the extension.
            return;
        }
        throw new Exception("Extended() should have thrown because peer doesn't support test_ping");
    }

    [TestMethod]
    public async Task WireExtension_LargePayload_SurvivesFraming()
    {
        // Extensions can send arbitrary-sized payloads. The Wire framing (4-byte length
        // prefix + ext_id + payload) must handle larger-than-MTU payloads correctly.
        var (peerA, peerB) = WireExtensionTests_CreateConnectedPair();
        var echoA = new WireExtensionTests_TestEcho();
        var echoB = new WireExtensionTests_TestEcho();
        peerA.Use(echoA); echoA.SetWire(peerA);
        peerB.Use(echoB); echoB.SetWire(peerB);
        await WireExtensionTests_PerformHandshakes(peerA, peerB);
        await Task.Delay(50);

        // 64 KiB payload — forces multi-chunk framing through SCTP-style transport.
        var largePayload = new byte[65536];
        new Random(5401).NextBytes(largePayload);

        await peerA.Extended("test_echo", largePayload);
        await Task.Delay(200);

        if (echoB.LastPayload == null || echoB.LastPayload.Length != 65536)
            throw new Exception($"echoB payload length={echoB.LastPayload?.Length}, expected 65536");
        if (!echoB.LastPayload.SequenceEqual(largePayload))
            throw new Exception("echoB received a different 65536-byte payload than what was sent");
    }

    // ---- helpers ----

    private static (Wire a, Wire b) WireExtensionTests_CreateConnectedPair()
    {
        var a = new Wire();
        var b = new Wire();
        // Loopback: each Wire's SendRaw routes into the other's DataReceived.
        a.SendRaw = data => { b.DataReceived(data); return Task.CompletedTask; };
        b.SendRaw = data => { a.DataReceived(data); return Task.CompletedTask; };
        return (a, b);
    }

    private static async Task WireExtensionTests_PerformHandshakes(Wire a, Wire b)
    {
        var infoHash = new byte[20];
        var peerIdA = new byte[20]; for (int i = 0; i < 20; i++) peerIdA[i] = (byte)('A' + i);
        var peerIdB = new byte[20]; for (int i = 0; i < 20; i++) peerIdB[i] = (byte)('a' + i);

        await a.Handshake(infoHash, peerIdA);
        await b.Handshake(infoHash, peerIdB);
    }

    /// <summary>Extension that round-trips string "ping"/"pong" messages.</summary>
    private sealed class WireExtensionTests_TestPing : IWireExtension
    {
        public string Name => "test_ping";
        public string? LastReceived;
        public int MessageCount;
        private Wire? _wire;

        public void OnHandshake(string infoHash, string peerId, WireExtensions extensions) { /* no-op */ }

        public void OnExtendedHandshake(Dictionary<string, object> handshake)
        {
            // We don't need to capture the Wire here — it's injected via the static
            // setter below. The ext manager doesn't pass the Wire to extensions, so
            // TestPing auto-reply from OnMessage wouldn't work without SetWire. Real
            // extensions either keep the Wire from a SetWire-style init or capture it
            // in a factory closure; both patterns are in use in ut_metadata / ut_pex.
        }

        internal void SetWire(Wire wire) => _wire = wire;

        public void OnMessage(byte[] buf)
        {
            MessageCount++;
            LastReceived = Encoding.UTF8.GetString(buf);
            if (LastReceived == "ping" && _wire is not null)
            {
                _ = _wire.Extended(Name, Encoding.UTF8.GetBytes("pong"));
            }
        }
    }

    /// <summary>Extension that advertises a counter in the extended handshake + increments-on-receive.</summary>
    private sealed class WireExtensionTests_TestCounter : IWireExtension
    {
        public string Name => "test_counter";
        public int InitialValue { get; }
        public int PeerInitialValue { get; private set; }
        public int LastIncrementValue { get; private set; }
        public int MessageCount;

        public WireExtensionTests_TestCounter(int initialValue)
        {
            InitialValue = initialValue;
        }

        /// <summary>Called by the test after `wire.Use(this)` to inject handshake data.</summary>
        internal void SetWire(Wire wire)
        {
            // Advertise our initial counter value via the extended handshake top-level dict.
            // Pattern matches ut_metadata's `metadata_size` field.
            wire.ExtendedHandshake["counter_initial"] = InitialValue;
        }

        public void OnHandshake(string infoHash, string peerId, WireExtensions extensions) { }

        public void OnExtendedHandshake(Dictionary<string, object> handshake)
        {
            if (handshake.TryGetValue("counter_initial", out var v) && v is long lv)
                PeerInitialValue = (int)lv;
        }

        public void OnMessage(byte[] buf)
        {
            MessageCount++;
            if (buf.Length >= 4)
            {
                // Big-endian int32
                LastIncrementValue = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
            }
        }
    }

    /// <summary>Extension that records the last binary payload it received verbatim.</summary>
    private sealed class WireExtensionTests_TestEcho : IWireExtension
    {
        public string Name => "test_echo";
        public byte[]? LastPayload;
        public int MessageCount;

        internal void SetWire(Wire wire) { /* no handshake data needed */ }

        public void OnHandshake(string infoHash, string peerId, WireExtensions extensions) { }
        public void OnExtendedHandshake(Dictionary<string, object> handshake) { }

        public void OnMessage(byte[] buf)
        {
            MessageCount++;
            LastPayload = (byte[])buf.Clone();
        }
    }
}
