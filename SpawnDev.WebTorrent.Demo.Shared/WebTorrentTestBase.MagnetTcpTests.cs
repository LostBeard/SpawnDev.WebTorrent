using System.Net;
using System.Security.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// A torrent added by INFO HASH ALONE (no .torrent file, no metadata) and given a peer by hand over TCP: the handshake has to go out, ut_metadata has to bring the info dict, and the
/// download has to complete. This is the path a BEP 46 subscriber takes (it learns only an info hash from the DHT), and every peer found later goes through the same AddPeer.
/// Desktop-only: browser has no TCP.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private static WebTorrentClient NewTcpOnlyClient() => new WebTorrentClient(new WebTorrentClientOptions
    {
        EnableTrackers = false, EnableDht = false, EnableLsd = false, EnableUtPex = false, DefaultTrackers = Array.Empty<string>(),
    });

    [TestMethod]
    public async Task Add_BareInfoHash_IsParsed_AndAMalformedOneThrowsInsteadOfReturningADeadTorrent()
    {
        await using var client = NewTcpOnlyClient();
        var hash = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

        var torrent = client.Add(hash);                                   // the doc: "Accepts magnet URI or info hash hex string"
        if (torrent.InfoHash != hash) throw new Exception($"a bare info hash was not parsed: InfoHash is '{torrent.InfoHash}'");
        if (torrent.WireInfoHashHex != hash) throw new Exception("WireInfoHashHex is empty: the handshake of every peer added to this torrent would carry no info hash");

        var upper = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        if (client.Add(upper).InfoHash != upper.ToLowerInvariant()) throw new Exception("an upper-case bare info hash was not normalised");

        foreach (var bad in new[] { "not a hash", "1234", new string('z', 40), "magnet:?dn=no-hash-here", "magnet:?xt=urn:btih:short" })
        {
            bool threw = false;
            try { client.Add(bad); } catch (Exception) { threw = true; }
            if (!threw) throw new Exception($"'{bad}' returned a torrent that can never connect to anyone: Add must say it is malformed");
        }
    }

    [TestMethod(RetryCount = 1)]
    public async Task Magnet_ByInfoHashOnly_WithManualTcpPeer_FetchesMetadataAndDownloads()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("TCP not available in browser");

        if (Environment.GetEnvironmentVariable("WT_VERBOSE") == "1") WebTorrentClient.VerboseLogging = true;
        await using var seeder = NewTcpOnlyClient();
        await seeder.EnsureTcpListenerAsync(0, IPAddress.Loopback);
        if (WebTorrentClient.VerboseLogging) seeder.TcpListener!.OnLog += Console.WriteLine;
        var big = RandomNumberGenerator.GetBytes(100_000);
        var small = new byte[] { 1, 2, 3, 4 };
        var seeded = await seeder.SeedAsync("magnet-tcp", new[] { ("dir/big.bin", big), ("small.bin", small) }, new TorrentCreatorOptions { HashAlgorithm = "SHA-1" });
        if (string.IsNullOrEmpty(seeded.InfoHash)) throw new Exception("the seeded torrent has no v1 info hash");

        await using var leecher = NewTcpOnlyClient();
        var dir = Path.Combine(Path.GetTempPath(), "magnet_tcp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var torrent = leecher.Add(seeded.InfoHash, new AddTorrentOptions { Path = dir });
            var tcp = new TcpPeer(initiator: true);
            await tcp.ConnectAsync($"127.0.0.1:{seeder.TcpListener!.LocalEndPoint.Port}");
            if (!tcp.Connected) throw new Exception("TCP connect to the seeder failed");
            torrent.AddPeer(tcp);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!torrent.Done && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!torrent.Done)
                throw new Exception($"magnet torrent did not complete in 20 s: metadata={(torrent.HasMetadata ? "yes" : "NO")}, peers={torrent.NumPeers}, progress={torrent.Progress:P0}, seeder accepted={seeder.TcpListener.AcceptedCount} rejected={seeder.TcpListener.RejectedCount}");

            var files = torrent.Files ?? throw new Exception("no files after completion");
            var bigFile = files.FirstOrDefault(f => f.Path.EndsWith("big.bin")) ?? throw new Exception("big.bin missing");
            var bytes = await bigFile.ReadAsync(0, (int)bigFile.Length);
            if (!bytes.AsSpan().SequenceEqual(big)) throw new Exception("downloaded bytes differ from the seeded ones");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
