using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.UnitTesting;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// REAL-WORLD service-worker video-streaming test. Drives an actual <see cref="HTMLVideoElement"/> pointed
/// at a torrent file's <c>StreamURL</c> — the EXACT path the demo and a real user use — over a live,
/// always-seeded open-source torrent (Sintel, web-seeded from webtorrent.io plus a WebRTC swarm). It proves
/// the whole chain works WHILE the torrent is still downloading:
/// <code>
///   &lt;video&gt;.src = file.StreamURL
///     -> webtorrent-sw.js fetch intercept
///     -> MessageChannel -> window client
///     -> ServiceWorkerStreamHandler.OnRequest -> WebTorrentClient.HandleStreamRequest
///     -> StreamRequest.RespondWithStream (pull-based ReadableStream, HTTP Range aware)
///     -> Torrent.ReadFileUint8ArrayAsync (pieces pulled on demand; bytes stay JS-side, no .NET hop)
///     -> chunks posted back to the SW -> &lt;video&gt; demuxes + plays
/// </code>
/// <c>loadedmetadata</c> proves the browser demuxed the streamed container (the moov range requests
/// worked); a mid-file seek proves HTTP Range / 206 seeking-while-downloading. Browser-only: there is no
/// service worker on desktop (the desktop streaming equivalent is TorrentHttpServer).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // Live, always-seeded open-source test torrent. Has an HTTP web seed (ws=) AND a WebRTC swarm, so it
    // streams in a browser even with zero local peers. Same magnet the demo ships as a quick-add chip.
    private const string SintelStreamMagnet =
        "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fsintel.torrent";

    // btih of SintelStreamMagnet — used to purge any persisted OPFS copy before the streaming test so it
    // reliably starts un-downloaded (see the purge in the test for why).
    private const string SintelInfoHash = "08ada5a7a6183aae1e09d831df6748d566095a10";

    // Live REMOTE swarm. The reference (SpawnDev.BlazorJS.WebTorrents) starts downloading this same magnet in
    // <10s, so this MUST be fast too — no inflated timeouts to paper over a slow start.
    [TestMethod(Timeout = 170000, RetryCount = 0)]
    public async Task Stream_ServiceWorker_RealVideoElement_StreamsAndSeeksWhileDownloading()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Service-worker video streaming is browser-only (no service worker on desktop)");

        WebTorrentClient.VerboseLogging = true;
        // Ensure the SW stream handler is actually listening. Its InitAsync (subscribes to
        // navigator.serviceWorker messages and answers /webtorrent/ stream requests) only runs when
        // Ready is awaited — if the IAsyncBackgroundService was never started, the SW posts the request
        // to the window and nobody replies, so the <video> (and any fetch) hangs at readyState=0.
        if (Client.StreamHandler != null) await Client.StreamHandler.Ready;

        // Count handler invocations: separates "the SW message never reaches ServiceWorkerStreamHandler"
        // (count stays 0 -> messaging/subscription bug) from "the handler got the request but never
        // replied" (count > 0 -> RespondWithStream / port bug).
        int onRequestCount = 0;
        var reqRanges = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Action<StreamRequest>? onReq = null;
        if (Client.StreamHandler != null)
        {
            onReq = r => { System.Threading.Interlocked.Increment(ref onRequestCount); reqRanges.Enqueue(r.RangeHeader ?? "(none)"); };
            Client.StreamHandler.OnRequest += onReq;
        }
        StreamRequest.DebugBytesByStartOffset.Clear();   // measure only THIS run's stream deliveries
        StreamRequest.DebugStreamLog.Clear();

        // The service worker must be installed AND controlling the page, or StreamURL fetches won't be
        // intercepted. Probe its health endpoint first so a non-controlling SW fails clearly instead of
        // surfacing as a confusing media error 60s later.
        using (var probe = await BlazorJSRuntime.JS.Fetch("/webtorrent-sw-check", new FetchOptions { Cache = "no-store" }))
        {
            if (probe.Status != 200)
                throw new Exception($"service worker is not controlling the page (/webtorrent-sw-check -> HTTP {probe.Status}); SW streaming cannot work");
        }

        // Force a clean download state. The PMT Chromium profile is persistent, so a prior run's OPFS copy
        // of this fixed-infohash torrent restores as Done — then file.Done is true before we stream a byte
        // and we cannot prove streaming-while-downloading (same persistent-profile gotcha handled in
        // InteropDesktopSeederTests). Clear any persisted copy first.
        {
            var stale = Client.Get(SintelInfoHash);
            if (stale != null) await Client.RemoveWithDataAsync(stale);
        }

        // Use the SHARED DI client — it is the one registered with the ServiceWorkerStreamHandler in
        // Program.cs, so its torrents are what HandleStreamRequest resolves StreamURL requests against.
        using var addCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var torrent = await Client.AddAsync(SintelStreamMagnet, ct: addCts.Token);

        HTMLVideoElement? video = null;
        Action<Event>? onMeta = null;
        Action<Event>? onSeeked = null;
        var videoEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var videoDetachers = new List<Action>();
        try
        {
            if (torrent.Files == null || torrent.Files.Length == 0)
                throw new Exception("torrent resolved metadata but exposed no files");

            var videoIdx = System.Array.FindIndex(torrent.Files,
                f => f.Type.StartsWith("video", StringComparison.OrdinalIgnoreCase));
            if (videoIdx < 0)
                throw new Exception("no video file in torrent; files: " +
                    string.Join(", ", torrent.Files.Select(f => $"{f.Name} ({f.Type})")));
            var file = torrent.Files[videoIdx];

            // The whole point: we begin streaming BEFORE the file has finished downloading.
            if (file.Done)
                throw new Exception("video file was already fully downloaded before streaming started — cannot prove streaming-while-downloading");

            var url = file.StreamURL;
            if (string.IsNullOrEmpty(url))
                throw new Exception("file.StreamURL was null/empty");

            video = new HTMLVideoElement { Muted = true, PlaysInline = true, AutoPlay = true, Preload = "auto" };

            // Attach to the DOM. A detached <video> can defer/skip media loading in Chromium; the demo and
            // the reference both point an in-document element at the stream URL.
            using (var doc = BlazorJSRuntime.JS.Get<SpawnDev.BlazorJS.JSObjects.Document>("document"))
            using (var body = doc.Body!)
                body.AppendChild(video);

            // ── loadedmetadata: browser demuxed the streamed container (moov range requests succeeded) ──
            var metaTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Full media-event trace — reveals the element's state machine (seeking/stalled/suspend/waiting)
            // so we can SEE why it stalls. Each handler is stored for -= in finally (ActionEvent discipline).
            var vt0 = DateTime.UtcNow;
            void VEvt(string n) => videoEvents.Enqueue($"{n}@{(int)(DateTime.UtcNow - vt0).TotalMilliseconds}rs{video!.ReadyState}");
            Action<Event> eLoadStart = _ => VEvt("loadstart"); video.OnLoadStart += eLoadStart; videoDetachers.Add(() => video.OnLoadStart -= eLoadStart);
            Action<Event> eDur = _ => VEvt("durationchange"); video.OnDurationChange += eDur; videoDetachers.Add(() => video.OnDurationChange -= eDur);
            Action<Event> eData = _ => VEvt("loadeddata"); video.OnLoadedData += eData; videoDetachers.Add(() => video.OnLoadedData -= eData);
            Action<Event> eProg = _ => VEvt("progress"); video.OnProgress += eProg; videoDetachers.Add(() => video.OnProgress -= eProg);
            Action<Event> eCan = _ => VEvt("canplay"); video.OnCanPlay += eCan; videoDetachers.Add(() => video.OnCanPlay -= eCan);
            Action<Event> ePlaying = _ => VEvt("playing"); video.OnPlaying += ePlaying; videoDetachers.Add(() => video.OnPlaying -= ePlaying);
            Action<Event> eSeeking = _ => VEvt("seeking"); video.OnSeeking += eSeeking; videoDetachers.Add(() => video.OnSeeking -= eSeeking);
            Action<Event> eStalled = _ => VEvt("stalled"); video.OnStalled += eStalled; videoDetachers.Add(() => video.OnStalled -= eStalled);
            Action<Event> eSuspend = _ => VEvt("suspend"); video.OnSuspend += eSuspend; videoDetachers.Add(() => video.OnSuspend -= eSuspend);
            Action<Event> eWaiting = _ => VEvt("waiting"); video.OnWaiting += eWaiting; videoDetachers.Add(() => video.OnWaiting -= eWaiting);
            Action<Event> eEmptied = _ => VEvt("emptied"); video.OnEmptied += eEmptied; videoDetachers.Add(() => video.OnEmptied -= eEmptied);

            onMeta = _ => { VEvt("loadedmetadata"); metaTcs.TrySetResult(); };
            video.OnLoadedMetadata += onMeta;

            video.Src = url;   // /webtorrent/{hash}/{idx} — intercepted by webtorrent-sw.js
            video.Load();

            // Poll our own download state while waiting for loadedmetadata — captures whether OUR client
            // is actually pulling head pieces (the reference streams this same swarm instantly, so any
            // stall here is this library's bug, not the swarm's).
            long maxDownloaded = 0; int maxPeers = 0;
            var metaDeadline = DateTime.UtcNow.AddSeconds(40);
            while (!metaTcs.Task.IsCompleted && DateTime.UtcNow < metaDeadline)
            {
                if (torrent.Downloaded > maxDownloaded) maxDownloaded = torrent.Downloaded;
                if (torrent.PeerCount > maxPeers) maxPeers = torrent.PeerCount;
                await Task.WhenAny(metaTcs.Task, Task.Delay(500));
            }
            if (!metaTcs.Task.IsCompleted)
            {
                var verr = video.Error;
                // What the VIDEO element actually pulled (captured BEFORE the diagnostic probes below add to
                // the same map). tail>0 => the element read its moov range; tail==0 => it never pulled it.
                long vFront = StreamRequest.DebugBytesByStartOffset.TryGetValue(0, out var vf0) ? vf0 : 0;
                long vTail = StreamRequest.DebugBytesByStartOffset.TryGetValue(128614400, out var vtb) ? vtb : 0;

                // Bounded probe: read the first 64 KiB the SW serves for this file and verify it is a real
                // mp4 (bytes 4..8 == "ftyp"). Splits "SW serves wrong/no bytes" from "video won't demux".
                int pStatus = -1, pBytes = -1; string pHead = "", pErr = "";
                int tStatus = -1, tBytes = -1, tMoov = -2; string tErr = "", tHead = "";
                async Task<(int st, int len, int moov, string head, string err)> Probe(string range)
                {
                    try
                    {
                        var popts = new FetchOptions { Headers = new Dictionary<string, string> { ["Range"] = range }, Cache = "no-store" };
                        async Task<(int, int, int, string)> Do()
                        {
                            using var presp = await BlazorJSRuntime.JS.Fetch(url, popts);
                            var st = presp.Status;
                            using var pab = await presp.ArrayBuffer();
                            using var pu8 = new Uint8Array(pab);
                            var bytes = pu8.ReadBytes();
                            int moovOff = -1;
                            for (int i = 0; i + 4 <= bytes.Length; i++)
                                if (bytes[i] == 0x6d && bytes[i + 1] == 0x6f && bytes[i + 2] == 0x6f && bytes[i + 3] == 0x76) { moovOff = i; break; }
                            // Parse the box AT the requested offset (start of served bytes): [4B size][4B type].
                            // For the tail this is what the demuxer reads first at bytes=N- — must be a valid box.
                            string box0 = "(short)";
                            if (bytes.Length >= 8)
                            {
                                long sz = ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
                                var typ = System.Text.Encoding.ASCII.GetString(bytes, 4, 4);
                                bool printable = true; foreach (var c in typ) if (c < 32 || c > 126) printable = false;
                                box0 = $"box0[size={sz} type='{(printable ? typ : "??")}']";
                            }
                            var head = box0 + " hex=" + Convert.ToHexString(bytes, 0, Math.Min(48, bytes.Length));
                            if (moovOff >= 4 && moovOff + 8 <= bytes.Length)
                                head += $" moovBox@{moovOff}=" + Convert.ToHexString(bytes, moovOff - 4, 12);
                            return (st, bytes.Length, moovOff, head);
                        }
                        var dt = Do();
                        if (await Task.WhenAny(dt, Task.Delay(15000)) == dt) { var r = await dt; return (r.Item1, r.Item2, r.Item3, r.Item4, ""); }
                        return (-1, -1, -2, "", "timed out 15s (SW blocked delivering bytes)");
                    }
                    catch (Exception pe) { return (-1, -1, -2, "", pe.Message); }
                }
                // Front 64 KiB (proves SW serves a real mp4 head) AND the video's EXACT tail range (proves the
                // SW serves the moov the <video> seeks for). tMoov >= 0 => SW delivers the moov correctly, so a
                // still-stalled video is a player-side read problem, not our SW/picker.
                (pStatus, pBytes, _, pHead, pErr) = await Probe("bytes=0-65535");
                (tStatus, tBytes, tMoov, tHead, tErr) = await Probe("bytes=128614400-");

                throw new Exception(
                    $"video never fired loadedmetadata — readyState={video.ReadyState}, " +
                    $"mediaErr={(verr != null ? $"code={verr.Code} msg='{verr.Message}'" : "null")}. " +
                    $"swHandler={(Client.StreamHandler != null)} onRequestCount={onRequestCount} reqRanges=[{string.Join(" | ", reqRanges)}]. " +
                    $"picker: {torrent.DebugSelectionState()}. videoDelivered: front={vFront} tail={vTail}. " +
                    $"videoEvents=[{string.Join(" ", videoEvents)}]. " +
                    $"streamLog=[{string.Join(" ; ", StreamRequest.DebugStreamLog)}]. " +
                    $"SW probe[0-65535]: status={pStatus} bytes={pBytes} head=[{pHead}] err='{pErr}'. " +
                    $"SW tailProbe[128614400-]: status={tStatus} bytes={tBytes} moovOff={tMoov} {tHead} err='{tErr}'. " +
                    $"torrent: progress={torrent.Progress:P2} downloaded={torrent.Downloaded} peers={torrent.PeerCount} webSeeds={torrent.WebSeedCount}. " +
                    $"file: progress={file.Progress:P2} downloaded={file.Downloaded}/{file.Length} type={file.Type}");
            }

            if (video.VideoWidth <= 0 || video.VideoHeight <= 0)
                throw new Exception($"loadedmetadata fired but dimensions are {video.VideoWidth}x{video.VideoHeight} — not a decodable video stream");
            var duration = video.Duration ?? 0;
            if (!(duration > 0) || double.IsInfinity(duration))
                throw new Exception($"streamed container yielded a non-finite duration ({duration})");

            // ── seek: forces a mid-file HTTP Range request -> 206 -> seeked. Proves seeking while downloading. ──
            var target = Math.Min(20.0, duration * 0.3);
            var seekTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onSeeked = _ => seekTcs.TrySetResult();
            video.OnSeeked += onSeeked;
            StreamRequest.DebugStreamLog.Clear();
            StreamRequest.DebugBytesByStartOffset.Clear();
            video.CurrentTime = target;

            if (await Task.WhenAny(seekTcs.Task, Task.Delay(TimeSpan.FromSeconds(30))) != seekTcs.Task)
            {
                var serr = video.Error;
                throw new Exception(
                    $"video never fired seeked after seeking to {target:F1}s — readyState={video.ReadyState} " +
                    $"currentTime={video.CurrentTime:F2} err={(serr != null ? $"code={serr.Code}" : "null")}. " +
                    $"picker: {torrent.DebugSelectionState()}. seekLog=[{string.Join(" ; ", StreamRequest.DebugStreamLog)}]");
            }

            if (Math.Abs(video.CurrentTime - target) > 1.5)
                throw new Exception($"after seeked, currentTime={video.CurrentTime:F2}s did not reach target {target:F2}s");

            // We proved metadata + seek while the file was NOT fully downloaded. Guard that invariant held.
            if (file.Done)
                throw new Exception("file finished downloading mid-test — re-run; this guards that we streamed a partial file");
        }
        finally
        {
            WebTorrentClient.VerboseLogging = false;
            if (onReq != null && Client.StreamHandler != null) Client.StreamHandler.OnRequest -= onReq;
            foreach (var d in videoDetachers) { try { d(); } catch { } }   // -= every traced media event before dispose
            if (video != null)
            {
                // Detach handlers with -= BEFORE disposing the element (BlazorJS ActionEvent discipline).
                if (onMeta != null) video.OnLoadedMetadata -= onMeta;
                if (onSeeked != null) video.OnSeeked -= onSeeked;
                try { video.Pause(); } catch { }
                video.Src = "";
                try { video.Remove(); } catch { }
                video.Dispose();
            }
            // RemoveWithDataAsync (not RemoveAsync): purge this run's OPFS pieces so the NEXT run starts
            // un-downloaded. RemoveAsync left the data behind, so a second run restored Sintel as Done and
            // failed the "streaming-while-downloading" precondition. Matches the sibling streaming tests.
            try { await Client.RemoveWithDataAsync(torrent); } catch { }
        }
    }

    /// <summary>Await a media-event Task or throw a clear, stage-specific error (surfacing video.Error) on timeout.</summary>
    private static async Task AwaitEventOrThrow(Task eventTask, HTMLVideoElement video, TimeSpan timeout, string message)
    {
        var done = await Task.WhenAny(eventTask, Task.Delay(timeout));
        if (done == eventTask) return;
        var err = video.Error;
        var detail = err != null ? " (video.error is set)" : "";
        throw new Exception($"{message}{detail} [readyState={video.ReadyState}, currentTime={video.CurrentTime:F2}]");
    }

    // ── ISOLATION: stream a fully-LOCAL (seeded, all pieces present) mp4 via OUR service worker to a real
    // <video>. No swarm, no download, no seeking-for-a-tail-moov — this proves whether OUR SW serving + the
    // <video> demux works AT ALL, separate from the live-swarm path. faststart.mp4 = moov at front (no seek
    // needed); nonfaststart.mp4 = moov at end (forces the demuxer to range-request the tail). Both ~14 KB.
    private async Task SeedLocalMp4AndDemux(string assetName, int timeoutSec)
    {
        if (Client.StreamHandler != null) await Client.StreamHandler.Ready;
        using (var swProbe = await BlazorJSRuntime.JS.Fetch("/webtorrent-sw-check", new FetchOptions { Cache = "no-store" }))
            if (swProbe.Status != 200)
                throw new Exception($"service worker is not controlling the page (/webtorrent-sw-check -> {swProbe.Status})");

        byte[] mp4;
        using (var resp = await BlazorJSRuntime.JS.Fetch(assetName, new FetchOptions { Cache = "no-store" }))
        {
            if (resp.Status != 200) throw new Exception($"could not fetch local asset /{assetName} -> HTTP {resp.Status}");
            using var ab = await resp.ArrayBuffer();
            using var u8 = new Uint8Array(ab);
            mp4 = u8.ReadBytes();
        }
        if (mp4.Length < 2000) throw new Exception($"/{assetName} fetched only {mp4.Length} bytes (asset missing?)");

        var torrent = await Client.SeedAsync(assetName, mp4);
        HTMLVideoElement? video = null;
        Action<Event>? onMeta = null;
        try
        {
            if (!torrent.Done)
                throw new Exception($"seeded /{assetName} is not Done ({torrent.Progress:P0}) — all pieces should be local");
            var file = torrent.Files![0];
            var url = file.StreamURL;

            video = new HTMLVideoElement { Muted = true, PlaysInline = true, AutoPlay = true, Preload = "auto" };
            using (var doc = BlazorJSRuntime.JS.Get<SpawnDev.BlazorJS.JSObjects.Document>("document"))
            using (var body = doc.Body!)
                body.AppendChild(video);
            var metaTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onMeta = _ => metaTcs.TrySetResult();
            video.OnLoadedMetadata += onMeta;
            video.Src = url;
            video.Load();

            if (await Task.WhenAny(metaTcs.Task, Task.Delay(timeoutSec * 1000)) != metaTcs.Task)
            {
                var verr = video.Error;
                throw new Exception(
                    $"/{assetName} ({mp4.Length}B, seeded + Done locally) streamed via OUR SW NEVER fired loadedmetadata: " +
                    $"readyState={video.ReadyState} err={(verr != null ? $"code={verr.Code} msg='{verr.Message}'" : "null")}. " +
                    $"streamLog=[{string.Join(" ; ", StreamRequest.DebugStreamLog)}]");
            }
            if (video.VideoWidth <= 0 || video.VideoHeight <= 0)
                throw new Exception($"/{assetName}: loadedmetadata fired but dimensions are {video.VideoWidth}x{video.VideoHeight}");
        }
        finally
        {
            if (video != null)
            {
                if (onMeta != null) video.OnLoadedMetadata -= onMeta;
                try { video.Pause(); } catch { }
                video.Src = "";
                try { video.Remove(); } catch { }
                video.Dispose();
            }
            try { await Client.RemoveWithDataAsync(torrent); } catch { }
        }
    }

    // OUR SW stream path with a codec the bundled Chromium CAN decode (VP9 WebM). Proves SW serving + demux
    // end to end. Playwright's Chromium lacks the H.264 decoder, so mp4s (incl. live-swarm Sintel) can't be
    // verified here — they play in real Chrome (the SpawnDev.com reference).
    [TestMethod(Timeout = 60000, RetryCount = 0)]
    public async Task Stream_VP9_ViaSW_Demuxes()
    {
        if (!OperatingSystem.IsBrowser()) throw new UnsupportedTestException("browser-only");
        StreamRequest.DebugStreamLog.Clear();
        await SeedLocalMp4AndDemux("test.webm", 25);
    }

    // TJ's SW-BYPASS isolation: whole seeded file -> Blob (BlobAsync, zero-copy OPFS) -> blob: URL -> <video>.
    // The service worker is NOT involved. Proves OUR byte assembly + the element/codec independent of the SW.
    [TestMethod(Timeout = 60000, RetryCount = 0)]
    public async Task Stream_VP9_ViaBlob_Demuxes()
    {
        if (!OperatingSystem.IsBrowser()) throw new UnsupportedTestException("browser-only");
        await SeedFileBlobToVideo("test.webm", 20);
    }

    // Confirms the chrome channel (real Chrome) decodes H.264 at all: plain static /faststart.mp4 direct.
    [TestMethod(Timeout = 30000, RetryCount = 0)]
    public async Task Stream_H264_Direct_Demuxes()
    {
        if (!OperatingSystem.IsBrowser()) throw new UnsupportedTestException("browser-only");
        HTMLVideoElement? video = null; Action<Event>? onMeta = null;
        try
        {
            video = new HTMLVideoElement { Muted = true, PlaysInline = true, AutoPlay = true, Preload = "auto" };
            using (var doc = BlazorJSRuntime.JS.Get<SpawnDev.BlazorJS.JSObjects.Document>("document"))
            using (var body = doc.Body!) body.AppendChild(video);
            var metaTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onMeta = _ => metaTcs.TrySetResult(); video.OnLoadedMetadata += onMeta;
            video.Src = "/faststart.mp4"; video.Load();
            if (await Task.WhenAny(metaTcs.Task, Task.Delay(20000)) != metaTcs.Task)
            {
                var verr = video.Error;
                throw new Exception($"DIRECT /faststart.mp4 (H.264) never demuxed: readyState={video.ReadyState} err={(verr != null ? $"code={verr.Code}" : "null")} — real-Chrome H.264 decoder not available");
            }
        }
        finally { if (video != null) { if (onMeta != null) video.OnLoadedMetadata -= onMeta; try { video.Pause(); } catch { } video.Src = ""; try { video.Remove(); } catch { } video.Dispose(); } }
    }

    // OUR SW + non-faststart H.264 (moov at END), seeded LOCALLY (all pieces present). Real Chrome decodes
    // H.264 and every piece is local, so the tail-moov seek must succeed instantly. Isolates OUR SW SEEK
    // handling from the live-swarm partial-download tail prioritization (the only thing flaky for big Sintel).
    [TestMethod(Timeout = 60000, RetryCount = 0)]
    public async Task Stream_SW_LocalNonFaststartH264_Demuxes()
    {
        if (!OperatingSystem.IsBrowser()) throw new UnsupportedTestException("browser-only");
        StreamRequest.DebugStreamLog.Clear();
        await SeedLocalMp4AndDemux("nonfaststart.mp4", 25);
    }

    // BASELINE: plain static /test.webm straight to the <video> (no torrent, no SW stream). Confirms the
    // bundled Chromium can decode VP9 at all — the env-only control for the two tests above. (H.264 mp4s fail
    // here AND in the reference demo under Playwright's Chromium; they need real Chrome's H.264 decoder.)
    [TestMethod(Timeout = 30000, RetryCount = 0)]
    public async Task Stream_VP9_Direct_Demuxes()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("browser-only");
        HTMLVideoElement? video = null;
        Action<Event>? onMeta = null;
        try
        {
            video = new HTMLVideoElement { Muted = true, PlaysInline = true, AutoPlay = true, Preload = "auto" };
            using (var doc = BlazorJSRuntime.JS.Get<SpawnDev.BlazorJS.JSObjects.Document>("document"))
            using (var body = doc.Body!)
                body.AppendChild(video);
            var metaTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onMeta = _ => metaTcs.TrySetResult();
            video.OnLoadedMetadata += onMeta;
            video.Src = "/test.webm";   // plain static VP9 file, NOT the SW torrent stream
            video.Load();
            if (await Task.WhenAny(metaTcs.Task, Task.Delay(20000)) != metaTcs.Task)
            {
                var verr = video.Error;
                throw new Exception(
                    $"DIRECT /test.webm (plain static, no SW stream) never demuxed: readyState={video.ReadyState} " +
                    $"err={(verr != null ? $"code={verr.Code} msg='{verr.Message}'" : "null")} — the env can't decode even plain VP9");
            }
            if (video.VideoWidth <= 0) throw new Exception($"direct webm loadedmetadata but dims {video.VideoWidth}x{video.VideoHeight}");
        }
        finally
        {
            if (video != null)
            {
                if (onMeta != null) video.OnLoadedMetadata -= onMeta;
                try { video.Pause(); } catch { }
                video.Src = "";
                try { video.Remove(); } catch { }
                video.Dispose();
            }
        }
    }

    // TJ's SW-BYPASS file-read isolation: read the whole seeded file into a Blob via TorrentFileInfo.BlobAsync
    // (exercises OUR cross-piece byte assembly from OPFS), make a blob: URL (NOT fetch-intercepted by the SW),
    // and feed it straight to the <video>. A pass proves our read/offsets are correct independent of the SW;
    // a fail here when the direct baseline plays would point at an offset/read bug, not the SW or the codec.
    private async Task SeedFileBlobToVideo(string assetName, int timeoutSec)
    {
        byte[] data;
        using (var resp = await BlazorJSRuntime.JS.Fetch(assetName, new FetchOptions { Cache = "no-store" }))
        {
            if (resp.Status != 200) throw new Exception($"could not fetch local asset /{assetName} -> HTTP {resp.Status}");
            using var ab = await resp.ArrayBuffer();
            using var u8 = new Uint8Array(ab);
            data = u8.ReadBytes();
        }
        if (data.Length < 2000) throw new Exception($"/{assetName} fetched only {data.Length} bytes");

        var torrent = await Client.SeedAsync(assetName, data);
        HTMLVideoElement? video = null;
        Action<Event>? onMeta = null;
        SpawnDev.BlazorJS.JSObjects.Blob? blob = null;
        string? objUrl = null;
        try
        {
            if (!torrent.Done) throw new Exception($"seeded /{assetName} not Done ({torrent.Progress:P0})");
            var file = torrent.Files![0];
            blob = await file.BlobAsync();
            if (blob == null) throw new Exception($"/{assetName}: BlobAsync returned null");
            objUrl = blob.ToObjectURL();   // blob: URL — the service worker is NOT involved

            video = new HTMLVideoElement { Muted = true, PlaysInline = true, AutoPlay = true, Preload = "auto" };
            using (var doc = BlazorJSRuntime.JS.Get<SpawnDev.BlazorJS.JSObjects.Document>("document"))
            using (var body = doc.Body!)
                body.AppendChild(video);
            var metaTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onMeta = _ => metaTcs.TrySetResult();
            video.OnLoadedMetadata += onMeta;
            video.Src = objUrl;
            video.Load();
            if (await Task.WhenAny(metaTcs.Task, Task.Delay(timeoutSec * 1000)) != metaTcs.Task)
            {
                var verr = video.Error;
                throw new Exception(
                    $"/{assetName} as a BLOB URL (SW BYPASSED, blobSize={blob.Size}, src={data.Length}B) never demuxed: " +
                    $"readyState={video.ReadyState} err={(verr != null ? $"code={verr.Code} msg='{verr.Message}'" : "null")} " +
                    $"— OUR file read (BlobAsync/offsets) produced bytes the element can't demux");
            }
            if (video.VideoWidth <= 0)
                throw new Exception($"/{assetName} blob: loadedmetadata fired but dimensions are {video.VideoWidth}x{video.VideoHeight}");
        }
        finally
        {
            if (objUrl != null) { try { SpawnDev.BlazorJS.JSObjects.URL.RevokeObjectURL(objUrl); } catch { } }
            blob?.Dispose();
            if (video != null)
            {
                if (onMeta != null) video.OnLoadedMetadata -= onMeta;
                try { video.Pause(); } catch { }
                video.Src = "";
                try { video.Remove(); } catch { }
                video.Dispose();
            }
            try { await Client.RemoveWithDataAsync(torrent); } catch { }
        }
    }
}
