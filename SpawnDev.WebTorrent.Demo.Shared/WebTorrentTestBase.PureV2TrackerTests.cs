using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Pure-v2 (v1-hash-absent) torrent support through the tracker + wire-handshake layer.
/// Pre-rc.13, a `magnet:?xt=urn:btmh:...` (v2-only) magnet threw
/// NotSupportedException and `Torrent.StartDiscovery` bailed on empty v1 InfoHash.
/// rc.13 adds a cross-client wire-compat shim: the 20-byte BitTorrent handshake value
/// and the tracker announce `info_hash` parameter use the first 20 bytes of the v2
/// SHA-256 hash for pure-v2 torrents (same convention as libtorrent/qBittorrent/rqbit).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private const string PureV2MagnetHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"; // 64 hex chars

    [TestMethod]
    public async Task PureV2_ParseMagnet_Accepted()
    {
        // Pre-rc.13: threw NotSupportedException. Now accepted.
        var client = CreateIsolatedClient();
        try
        {
            var torrent = client.Add($"magnet:?xt=urn:btmh:1220{PureV2MagnetHash}&dn=v2only");
            if (string.IsNullOrEmpty(torrent.V2InfoHash)) throw new Exception("V2InfoHash not populated from magnet");
            if (!string.IsNullOrEmpty(torrent.InfoHash)) throw new Exception($"v1 InfoHash unexpectedly set to {torrent.InfoHash}");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task PureV2_WireInfoHashHex_Returns20BytesOfV2Hash()
    {
        var client = CreateIsolatedClient();
        try
        {
            var torrent = client.Add($"magnet:?xt=urn:btmh:1220{PureV2MagnetHash}&dn=v2only");
            var wireHex = torrent.WireInfoHashHex;
            if (wireHex.Length != 40) throw new Exception($"WireInfoHashHex should be 40 chars (20 bytes hex), got {wireHex.Length}");

            var expected = PureV2MagnetHash[..40].ToLowerInvariant();
            if (wireHex != expected)
                throw new Exception($"WireInfoHashHex={wireHex}, expected {expected} (first 20 bytes of V2InfoHash)");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task PureV2_ClientGet_FindsByV1Truncation()
    {
        var client = CreateIsolatedClient();
        try
        {
            var torrent = client.Add($"magnet:?xt=urn:btmh:1220{PureV2MagnetHash}&dn=v2only");

            // Lookup by truncated v2 hash (the 20-byte wire value)
            var wireHex = PureV2MagnetHash[..40].ToLowerInvariant();
            var byWire = client.Get(wireHex);
            if (byWire is null) throw new Exception("client.Get(wireHex) returned null for pure-v2 torrent");
            if (!ReferenceEquals(byWire, torrent)) throw new Exception("client.Get returned wrong torrent instance");

            // Also lookup by full v2 hash
            var byV2 = client.Get(PureV2MagnetHash);
            if (byV2 is null) throw new Exception("client.Get(fullV2Hash) returned null");
            if (!ReferenceEquals(byV2, torrent)) throw new Exception("client.Get by full V2 returned wrong torrent");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task V1Only_WireInfoHashHex_ReturnsV1()
    {
        // Sanity: v1 path unchanged. WireInfoHashHex returns v1 hash verbatim.
        var v1Hex = "aaaabbbbccccddddeeeeffff1111222233334444"; // 40 chars
        var client = CreateIsolatedClient();
        try
        {
            var torrent = client.Add($"magnet:?xt=urn:btih:{v1Hex}&dn=v1only");
            if (torrent.WireInfoHashHex != v1Hex)
                throw new Exception($"WireInfoHashHex={torrent.WireInfoHashHex}, expected v1 hash {v1Hex}");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Hybrid_WireInfoHashHex_PrefersV1()
    {
        // Hybrid: BEP 52 upgrade contract says tracker announce uses v1 hash so v1-only
        // peers can still find the swarm. Ensure WireInfoHashHex returns v1 when both are set.
        var v1Hex = "aaaabbbbccccddddeeeeffff1111222233334444";
        var client = CreateIsolatedClient();
        try
        {
            var torrent = client.Add($"magnet:?xt=urn:btih:{v1Hex}&xt=urn:btmh:1220{PureV2MagnetHash}&dn=hybrid");
            if (torrent.InfoHash != v1Hex) throw new Exception($"InfoHash={torrent.InfoHash}, expected {v1Hex}");
            if (torrent.V2InfoHash != PureV2MagnetHash) throw new Exception($"V2InfoHash mismatch");
            if (torrent.WireInfoHashHex != v1Hex)
                throw new Exception($"Hybrid WireInfoHashHex={torrent.WireInfoHashHex}, expected v1 {v1Hex}");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task V1Only_WireInfoHashHex_PureV2_BytesMatchV2Prefix()
    {
        // Sanity: bytes returned by Convert.FromHexString(WireInfoHashHex) must match the
        // first 20 bytes of Convert.FromHexString(V2InfoHash). This is what downstream
        // trackers + wire handshake code uses.
        var client = CreateIsolatedClient();
        try
        {
            var torrent = client.Add($"magnet:?xt=urn:btmh:1220{PureV2MagnetHash}&dn=v2only");
            var wireBytes = Convert.FromHexString(torrent.WireInfoHashHex);
            var v2Bytes = Convert.FromHexString(torrent.V2InfoHash);
            if (wireBytes.Length != 20) throw new Exception($"wireBytes length={wireBytes.Length}, expected 20");
            for (int i = 0; i < 20; i++)
            {
                if (wireBytes[i] != v2Bytes[i])
                    throw new Exception($"wireBytes[{i}]={wireBytes[i]} != v2Bytes[{i}]={v2Bytes[i]}");
            }
        }
        finally { await client.DisposeAsync(); }
    }
}
