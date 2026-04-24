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

    [TestMethod]
    public async Task PureV2_Client_UtMetadataFactory_WiresV2Mode()
    {
        // End-to-end: client.Add(pureV2Magnet) registers the ut_metadata factory. When a
        // wire is created and ApplyExtensions runs, the factory should produce a
        // UtMetadataExtension with MetadataVersion=2 and V2InfoHashHex populated from
        // the torrent's state. rc.17 wired this so pure-v2 magnets actually advertise
        // metadata_version=2 in their BEP 10 extended handshake.
        var client = CreateIsolatedClient();
        try
        {
            var torrent = client.Add($"magnet:?xt=urn:btmh:1220{PureV2MagnetHash}&dn=v2only");

            // Use a Wire directly to fire the extension factory. ApplyExtensions is
            // internal but the Wire.Use API is what matters — we can invoke the factory
            // manually to inspect what it produces.
            var wire = new SpawnDev.WebTorrent.Wire();
            wire.SendRaw = _ => Task.CompletedTask;
            client.ApplyExtensions(wire);

            var utm = wire.GetExtension<SpawnDev.WebTorrent.UtMetadataExtension>();
            if (utm is null) throw new Exception("ApplyExtensions didn't register UtMetadataExtension on wire");
            if (utm.MetadataVersion != 2)
                throw new Exception($"pure-v2 torrent should set UtMetadataExtension.MetadataVersion=2, got {utm.MetadataVersion}");
            if (utm.V2InfoHashHex != PureV2MagnetHash)
                throw new Exception($"UtMetadataExtension.V2InfoHashHex should be {PureV2MagnetHash}, got {utm.V2InfoHashHex}");

            // Extended handshake dict should carry the metadata_version=2 key.
            if (!wire.ExtendedHandshake.TryGetValue("metadata_version", out var mv))
                throw new Exception("wire.ExtendedHandshake should contain metadata_version key");
            var mvInt = mv switch { long l => (int)l, int i => i, _ => 0 };
            if (mvInt != 2) throw new Exception($"metadata_version={mvInt}, expected 2");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task PureV2_AddSameMagnetTwice_Dedups()
    {
        // rc.20 regression: pre-fix, two Add() calls with the same pure-v2 magnet produced
        // TWO Torrent entries because the duplicate check compared on v1 InfoHash (empty
        // for pure-v2). rc.20 compares on WireInfoHashHex which falls back to v2.
        var client = CreateIsolatedClient();
        try
        {
            var magnet = $"magnet:?xt=urn:btmh:1220{PureV2MagnetHash}&dn=dedup-test";
            var t1 = client.Add(magnet);
            var t2 = client.Add(magnet);
            if (!ReferenceEquals(t1, t2))
                throw new Exception("two Add() calls with same pure-v2 magnet should return the same Torrent instance");
            if (client.Torrents.Count != 1)
                throw new Exception($"Torrents.Count={client.Torrents.Count}, expected 1 after dedup");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task PureV2_RemoveAsyncByHash_ResolvesV2Hash()
    {
        // rc.20: RemoveAsync(infoHash) now routes through Get() so pure-v2 callers can
        // remove by the full v2 hash (64 chars) or the wire-truncated version (40 chars).
        var client = CreateIsolatedClient();
        try
        {
            var magnet = $"magnet:?xt=urn:btmh:1220{PureV2MagnetHash}&dn=remove-test";
            var torrent = client.Add(magnet);
            if (client.Torrents.Count != 1) throw new Exception("setup: expected 1 torrent");

            // Remove by full v2 hash
            await client.RemoveAsync(PureV2MagnetHash);
            if (client.Torrents.Count != 0)
                throw new Exception($"RemoveAsync(fullV2) failed; Torrents.Count={client.Torrents.Count}");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task PureV2_RemoveWithDataAsyncByHash_ResolvesV2Hash()
    {
        // rc.20/rc.21: RemoveWithDataAsync(string) also routes through Get() so the full
        // v2 hash and the wire-truncated form both resolve to the same torrent instance.
        var client = CreateIsolatedClient();
        try
        {
            var magnet = $"magnet:?xt=urn:btmh:1220{PureV2MagnetHash}&dn=remove-with-data-test";
            var torrent = client.Add(magnet);
            if (client.Torrents.Count != 1) throw new Exception("setup: expected 1 torrent");

            // Remove by wire-truncated v2 hash (40 chars)
            var wireHex = PureV2MagnetHash[..40].ToLowerInvariant();
            await client.RemoveWithDataAsync(wireHex);
            if (client.Torrents.Count != 0)
                throw new Exception($"RemoveWithDataAsync(wireHex) failed; Torrents.Count={client.Torrents.Count}");
        }
        finally { await client.DisposeAsync(); }
    }

    [TestMethod]
    public async Task PureV2_TorrentMetadata_WireInfoHashHex_FallsBackToV2()
    {
        // Pure-v2 TorrentMetadata's WireInfoHashHex mirrors Torrent's: first 20 bytes of
        // V2InfoHash when v1 InfoHash is empty. Used by RestoreFromStorageAsync to build
        // the state filename for pure-v2 torrents.
        var metadata = new TorrentMetadata
        {
            InfoHash = "",
            V2InfoHash = PureV2MagnetHash,
        };
        var expected = PureV2MagnetHash[..40].ToLowerInvariant();
        if (metadata.WireInfoHashHex != expected)
            throw new Exception($"TorrentMetadata.WireInfoHashHex={metadata.WireInfoHashHex}, expected {expected}");

        // Hybrid: v1 wins (BEP 52 upgrade contract).
        var hybridMeta = new TorrentMetadata
        {
            InfoHash = "aaaabbbbccccddddeeeeffff1111222233334444",
            V2InfoHash = PureV2MagnetHash,
        };
        if (hybridMeta.WireInfoHashHex != "aaaabbbbccccddddeeeeffff1111222233334444")
            throw new Exception($"Hybrid TorrentMetadata.WireInfoHashHex wrong: {hybridMeta.WireInfoHashHex}");

        // Both empty → empty.
        var empty = new TorrentMetadata();
        if (empty.WireInfoHashHex != "")
            throw new Exception($"Empty TorrentMetadata should yield empty WireInfoHashHex, got '{empty.WireInfoHashHex}'");

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task V1Only_Client_UtMetadataFactory_StaysOnV1()
    {
        // Mirror: v1-only magnet → factory produces a default (v1) UtMetadataExtension.
        var v1Hex = "aaaabbbbccccddddeeeeffff1111222233334444";
        var client = CreateIsolatedClient();
        try
        {
            var torrent = client.Add($"magnet:?xt=urn:btih:{v1Hex}&dn=v1only");

            var wire = new SpawnDev.WebTorrent.Wire();
            wire.SendRaw = _ => Task.CompletedTask;
            client.ApplyExtensions(wire);

            var utm = wire.GetExtension<SpawnDev.WebTorrent.UtMetadataExtension>();
            if (utm is null) throw new Exception("ApplyExtensions didn't register UtMetadataExtension");
            if (utm.MetadataVersion != 1)
                throw new Exception($"v1-only torrent should keep UtMetadataExtension.MetadataVersion=1, got {utm.MetadataVersion}");
            if (!string.IsNullOrEmpty(utm.V2InfoHashHex))
                throw new Exception($"v1-only torrent should have empty V2InfoHashHex, got {utm.V2InfoHashHex}");

            if (wire.ExtendedHandshake.ContainsKey("metadata_version"))
                throw new Exception("v1-only torrent must NOT advertise metadata_version in its extended handshake");
        }
        finally { await client.DisposeAsync(); }
    }
}
