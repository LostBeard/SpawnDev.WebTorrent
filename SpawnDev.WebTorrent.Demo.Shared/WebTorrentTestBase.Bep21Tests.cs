using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>BEP 21 partial seed tests; run in the existing shared desktop and browser test suites.
/// https://www.bittorrent.org/beps/bep_0021.html</summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Bep21_PartialSeedState_ExcludesPauseAndFullSeeds()
    {
        bool[] pieces = { true, true, false, false };
        var selected = new (int From, int To)[] { (0, 1) };
        if (!Bep21State.IsPartialSeed(true, false, false, pieces, selected, Array.Empty<int>()))
            throw new Exception("All wanted pieces present: expected partial seed");
        if (Bep21State.IsPartialSeed(true, true, false, pieces, selected, Array.Empty<int>()))
            throw new Exception("Torrent.Pause is not a partial seed");
        if (Bep21State.IsPartialSeed(false, false, false, pieces, selected, Array.Empty<int>())
            || Bep21State.IsPartialSeed(true, false, true, pieces, selected, Array.Empty<int>()))
            throw new Exception("Metadata and an active torrent are required");
        if (Bep21State.IsPartialSeed(true, false, false,
            new[] { true, true, true, true }, selected, Array.Empty<int>()))
            throw new Exception("A complete seed must not be a partial seed");
        if (Bep21State.IsPartialSeed(true, false, false,
            new[] { false, false, false, false }, selected, Array.Empty<int>()))
            throw new Exception("An empty peer must not be a partial seed");
        if (Bep21State.IsPartialSeed(true, false, false, pieces,
            Array.Empty<(int From, int To)>(), Array.Empty<int>()))
            throw new Exception("Default full download still wants missing pieces");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep21_NewSelectedOrCriticalPiece_ExitsPartialSeed()
    {
        bool[] pieces = { true, true, false, false };
        if (Bep21State.IsPartialSeed(true, false, false, pieces,
            new (int From, int To)[] { (0, 2) }, Array.Empty<int>()))
            throw new Exception("Unverified selected piece requires downloading");
        if (Bep21State.IsPartialSeed(true, false, false, pieces,
            new (int From, int To)[] { (0, 1) }, new[] { 2 }))
            throw new Exception("On-demand missing critical piece requires downloading");
        if (!Bep21State.IsPartialSeed(true, false, false, pieces,
            new (int From, int To)[] { (0, 1) }, new[] { 0 }))
            throw new Exception("Already available critical piece requires no download");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep21_TrackerLeft_CountsOnlyVerifiedTorrentWideBytes()
    {
        bool[] pieces = { true, false, true, false };
        if (Bep21State.VerifiedBytes(pieces, 13, 4) != 8)
            throw new Exception("Expected 8 verified bytes");
        if (Bep21State.VerifiedBytes(new[] { true, true, true, true }, 13, 4) != 13)
            throw new Exception("Last piece length must be clamped");
        var torrent = new Torrent
        {
            Length = 13, PieceLength = 4, LastPieceLength = 1,
            Bitfield = pieces, UploadedTotal = 77
        };
        var opts = torrent.CurrentAnnounceOptions();
        if (opts.Left != 5 || opts.Uploaded != 77)
            throw new Exception($"Expected left=5 and uploaded=77, got {opts.Left}/{opts.Uploaded}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep21_PeriodicOptions_RetainPausedAndCurrentStats()
    {
        var start = Discovery.PeriodicOptions(new AnnounceOptions
        {
            Event = "started", Port = 44111, Left = 99,
            Uploaded = 12, Downloaded = 25, Numwant = 3
        });
        if (start.Event != null || start.Left != 99 || start.Port != 44111
            || start.Uploaded != 12 || start.Downloaded != 25 || start.Numwant != 3)
            throw new Exception("Started should clear; all counters and port should persist");
        var paused = Discovery.PeriodicOptions(new AnnounceOptions
        {
            Event = "paused", Left = 21, Uploaded = 94, Downloaded = 100
        });
        if (paused.Event != "paused" || paused.Left != 21
            || paused.Uploaded != 94 || paused.Downloaded != 100)
            throw new Exception("Paused status and current counters must persist");
        if (Discovery.PeriodicOptions(new AnnounceOptions { Event = "completed" }).Event != null)
            throw new Exception("Completed must not repeat on periodic announces");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep21_UdpPaused_UsesLibtorrentEventCodeFour()
    {
        if (UdpTrackerClient.ParseEvent("paused") != UdpTrackerClient.AnnounceEvent.Paused
            || UdpTrackerClient.ToWireEventCode(UdpTrackerClient.AnnounceEvent.Paused) != 4
            || UdpTrackerClient.ToWireEventCode(UdpTrackerClient.AnnounceEvent.Started) != 2)
            throw new Exception("UDP event mapping mismatch");
        byte[] request = new byte[98];
        UdpTrackerClient.WriteInt32BE(request, 80,
            UdpTrackerClient.ToWireEventCode(UdpTrackerClient.AnnounceEvent.Paused));
        if (!request.Skip(80).Take(4).SequenceEqual(new byte[] { 0, 0, 0, 4 })
            || UdpTrackerClient.ReadInt32BE(request, 80) != 4)
            throw new Exception("UDP event 4 must be big-endian at offset 80");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep21_TrackerResponse_DownloadersOptional()
    {
        var resp = new TrackerAnnounceResponse { Complete = 2, Incomplete = 4 };
        if (JsonSerializer.Serialize(resp).Contains("\"downloaders\""))
            throw new Exception("Absent downloaders must be omitted");
        resp.Downloaders = 3;
        if (!JsonSerializer.Serialize(resp).Contains("\"downloaders\":3"))
            throw new Exception("Present downloaders must serialize");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep21_ExtendedHandshake_UpdatesUploadOnlyOnExistingWire()
    {
        var a = new Wire();
        var b = new Wire();
        a.SendRaw = data => { b.DataReceived(data); return Task.CompletedTask; };
        b.SendRaw = data => { a.DataReceived(data); return Task.CompletedTask; };
        try
        {
            await a.SetUploadOnlyAsync(true);
            await a.Handshake(new byte[20], Enumerable.Repeat((byte)'A', 20).ToArray());
            await b.Handshake(new byte[20], Enumerable.Repeat((byte)'B', 20).ToArray());
            await Task.Delay(150);
            if (b.PeerExtendedHandshake == null
                || Convert.ToInt64(b.PeerExtendedHandshake["upload_only"]) != 1)
                throw new Exception("Initial upload_only=1 was not delivered");
            await a.SetUploadOnlyAsync(false);
            await Task.Delay(100);
            if (Convert.ToInt64(b.PeerExtendedHandshake["upload_only"]) != 0)
                throw new Exception("Live upload_only=0 update was not delivered");
        }
        finally { a.Destroy(); b.Destroy(); }
    }

    [TestMethod]
    public async Task Bep21_HttpTracker_RetriesPausedWithCurrentStatistics()
    {
        using var handler = new Bep21RecordingHandler();
        using var http = new HttpClient(handler);
        var current = new AnnounceOptions
        {
            Event = "paused", Port = 44111, Left = 99, Uploaded = 12, Downloaded = 25
        };
        await using var discovery = new Discovery(
            new byte[20], Enumerable.Repeat((byte)'A', 20).ToArray(),
            new[] { "http://tracker.invalid/announce" },
            _ => throw new InvalidOperationException("No WebRTC tracker in this test"),
            http, () => current);
        await discovery.AnnounceAsync(new AnnounceOptions
        {
            Event = "started", Port = 44111, Left = 99, Uploaded = 12, Downloaded = 25
        });

        for (int attempt = 0; attempt < 50 && handler.Requests.Count < 2; attempt++)
            await Task.Delay(100);
        var requests = handler.Requests.Select(u => u.Query).ToArray();
        if (requests.Length < 2
            || !requests[0].Contains("event=started")
            || !requests[1].Contains("event=paused")
            || !requests[1].Contains("left=99")
            || !requests[1].Contains("uploaded=12")
            || !requests[1].Contains("downloaded=25")
            || !requests[1].Contains("port=44111"))
            throw new Exception($"Paused reannounce lost state: {string.Join(" | ", requests)}");

        current = new AnnounceOptions
        {
            Event = null, Port = 44111, Left = 50, Uploaded = 42, Downloaded = 75
        };
        for (int attempt = 0; attempt < 50 && handler.Requests.Count < 3; attempt++)
            await Task.Delay(100);
        requests = handler.Requests.Select(u => u.Query).ToArray();
        if (requests.Length < 3 || requests[2].Contains("event=paused")
            || !requests[2].Contains("left=50") || !requests[2].Contains("uploaded=42"))
            throw new Exception($"Unpaused reannounce did not track live state: {string.Join(" | ", requests)}");
    }

    private sealed class Bep21RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("d8:intervali1e5:peers0:e"))
            });
        }
    }
}
