using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpawnDev.UnitTesting;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Live integration against the hub's generic source proxy - <c>GET /src?url=...</c>.
///
/// <para>
/// The hub had a hand-written proxy per source (HuggingFace, Ollama), so every new origin meant new code.
/// The generic form takes any host on a configured allowlist, adds CORS, serves ranges, caches, and can
/// reach INSIDE an archive - which turns a packaged model into a plain URL. That last part is what the
/// lazy-hash streamer needs, because it only ever wanted a URL.
/// </para>
///
/// <para>
/// ⚠️ These SKIP rather than fail while the deployed hub predates the feature, detected by
/// <c>/src/allowed</c> answering 404. A test that cannot pass until an operator deploys is not a red gate,
/// it is a gate that is not armed yet - but it must say which of those it is, so the skip message names
/// the reason instead of the run quietly showing green.
/// </para>
/// </summary>
public abstract partial class WebTorrentTestBase
{
    const string SrcHubBaseUrl = "https://hub.spawndev.com:44365";

    /// <summary>A small, real file on an allowlisted host - ZipVoice's token table, 2,570 bytes.</summary>
    const string SrcSampleUrl =
        "https://huggingface.co/k2-fsa/ZipVoice/resolve/main/zipvoice_distill/tokens.txt";

    /// <summary>Skip unless the deployed hub actually has the source proxy.</summary>
    private static async Task<HttpClient> RequireSourceProxyAsync()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        HttpResponseMessage probe;
        try { probe = await http.GetAsync($"{SrcHubBaseUrl}/src/allowed"); }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"hub unreachable ({ex.GetType().Name}) - needs internet");
        }
        if (probe.StatusCode == HttpStatusCode.NotFound)
            throw new UnsupportedTestException(
                "the deployed hub has no /src endpoint yet - deploy a build with SourceProxy to arm this");
        return http;
    }

    [TestMethod(Timeout = 300000)]
    public async Task SourceProxy_PublishesItsAllowlist()
    {
        using var http = await RequireSourceProxyAsync();
        var json = await http.GetStringAsync($"{SrcHubBaseUrl}/src/allowed");
        using var doc = JsonDocument.Parse(json);
        var hosts = doc.RootElement.GetProperty("allowedHosts").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();

        // An allowlist nobody can read is folklore: a caller would have to discover it by trial and error.
        if (hosts.Length == 0)
            throw new Exception("the hub publishes an EMPTY allowlist - it will refuse every request");
        Console.WriteLine($"[SourceProxy] allowlist: {string.Join(", ", hosts)}");
    }

    [TestMethod(Timeout = 300000)]
    public async Task SourceProxy_RefusesHostsNotOnTheAllowlist()
    {
        using var http = await RequireSourceProxyAsync();

        // The allowlist IS the security boundary. An open proxy would let anyone launder requests through
        // the hub and spend its bandwidth, so this asserts the refusal rather than assuming it.
        var res = await http.GetAsync($"{SrcHubBaseUrl}/src?url=https://example.com/whatever.bin");
        if (res.StatusCode != HttpStatusCode.Forbidden)
            throw new Exception($"expected 403 for an unlisted host, got {(int)res.StatusCode}. "
                              + "An open proxy is not a lesser version of this feature - it is a different one.");

        // ...and the classic SSRF shape, which bypasses host naming entirely.
        var ip = await http.GetAsync($"{SrcHubBaseUrl}/src?url=https://127.0.0.1/x");
        if (ip.StatusCode != HttpStatusCode.Forbidden)
            throw new Exception($"expected 403 for a literal IP, got {(int)ip.StatusCode}");

        Console.WriteLine("[SourceProxy] refuses unlisted hosts and literal IPs");
    }

    [TestMethod(Timeout = 600000)]
    public async Task SourceProxy_ServesAFileWithRangesAndCors()
    {
        using var http = await RequireSourceProxyAsync();
        var url = $"{SrcHubBaseUrl}/src?url={Uri.EscapeDataString(SrcSampleUrl)}";

        var full = await http.GetAsync(url);
        full.EnsureSuccessStatusCode();
        var bytes = await full.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0) throw new Exception("proxy returned an empty body");

        // ⚠️ Do NOT assert on Access-Control-Allow-Origin or -Expose-Headers here. A browser CONSUMES the
        // CORS protocol headers and never exposes them to fetch, so those checks cannot pass in the browser
        // lane however correct the server is - I wrote them that way first and they failed against a hub
        // that curl showed was sending both. The meaningful browser-side proof is different and stronger:
        // this request is CROSS-ORIGIN, so if the server had not sent Allow-Origin the fetch would have
        // thrown rather than returning a body at all. Reaching this line IS the assertion.
        // Content-Range being READABLE below is likewise the real test of Expose-Headers: a browser hides
        // that header unless the server named it.

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 15);
        var partial = await http.SendAsync(req);
        if (partial.StatusCode != HttpStatusCode.PartialContent)
            throw new Exception($"expected 206 for a Range request, got {(int)partial.StatusCode} - "
                              + "the lazy-hash streamer seeks, so a proxy that ignores Range is unusable");
        // Readable only because the server listed it in Access-Control-Expose-Headers.
        if (partial.Content.Headers.ContentRange == null)
            throw new Exception("Content-Range is not readable from a cross-origin response - the server "
                              + "must name it in Access-Control-Expose-Headers or a seeking reader is blind");
        var slice = await partial.Content.ReadAsByteArrayAsync();
        if (slice.Length != 16)
            throw new Exception($"asked for 16 bytes, got {slice.Length}");
        if (!slice.SequenceEqual(bytes.Take(16)))
            throw new Exception("the ranged bytes do not match the same span of the full body");

        Console.WriteLine($"[SourceProxy] served {bytes.Length} bytes, range 0-15 matches, CORS complete");
    }

    [TestMethod(Timeout = 1800000)]
    public async Task SourceProxy_ReadsAMemberOutOfAnArchive()
    {
        using var http = await RequireSourceProxyAsync();

        // sherpa-onnx ships every model as .tar.bz2, and a compressed archive CANNOT be seeked into - so
        // without this the only options were "host each file we need" or "make every visitor download the
        // whole archive". ZipVoice's vocoder is the case that forced it: it exists nowhere else per-file.
        const string archive =
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/sherpa-onnx-zipvoice-distill-int8-zh-en-emilia.tar.bz2";
        const string member = "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia/tokens.txt";

        // ⚠️ First contact with an archive is expensive and it is the hub, not this test, that pays it:
        // 109 MB fetched from GitHub, then bzip2-decompressed from the start just to walk the member headers.
        // The hub no longer does either inside the request - it answers 202 Accepted and works in the
        // background - because holding the connection open for that long does not merely feel slow, it gets
        // the request KILLED by the gateway in front of the hub (measured: 504 at exactly 25 seconds, while
        // the hub was still fetching perfectly happily).
        //
        // So 202 is a normal, expected answer here and NOT a failure. Poll until it turns into 200.
        string? listJson = null;
        var listUrl = $"{SrcHubBaseUrl}/src/list?url={Uri.EscapeDataString(archive)}";
        var warmClock = System.Diagnostics.Stopwatch.StartNew();
        while (warmClock.Elapsed < TimeSpan.FromMinutes(20))
        {
            HttpResponseMessage listRes;
            try { listRes = await http.GetAsync(listUrl); }
            catch (Exception ex)
            {
                Console.WriteLine($"[SourceProxy] list request failed ({ex.Message}); retrying");
                await Task.Delay(TimeSpan.FromSeconds(15));
                continue;
            }
            if (listRes.StatusCode == HttpStatusCode.Accepted)
            {
                Console.WriteLine($"[SourceProxy] hub still warming the archive "
                                + $"({warmClock.Elapsed.TotalSeconds:F0}s): {await listRes.Content.ReadAsStringAsync()}");
                await Task.Delay(TimeSpan.FromSeconds(10));
                continue;
            }
            if (!listRes.IsSuccessStatusCode)
                throw new Exception($"listing the archive returned {(int)listRes.StatusCode}: "
                                  + await listRes.Content.ReadAsStringAsync());
            listJson = await listRes.Content.ReadAsStringAsync();
            break;
        }
        if (listJson == null) throw new Exception("the hub did not finish warming the archive within 20 minutes");
        using var doc = JsonDocument.Parse(listJson);
        if (!doc.RootElement.TryGetProperty("entries", out var entriesEl))
            throw new Exception("a 200 listing carried no 'entries' - 200 must mean the listing is READY, "
                              + "or a caller polling on the status code proceeds to parse a progress report");
        var count = entriesEl.GetArrayLength();
        if (count == 0) throw new Exception("archive listing is empty");

        var res = await http.GetAsync(
            $"{SrcHubBaseUrl}/src?url={Uri.EscapeDataString(archive)}&member={Uri.EscapeDataString(member)}");
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync();

        // tokens.txt is 2,570 bytes in this package; an exact size proves we got THAT member rather than
        // some other file, or a truncated extraction.
        if (bytes.Length != 2570)
            throw new Exception($"expected the 2,570-byte tokens.txt, got {bytes.Length} bytes");

        // A path that would escape the cache directory must be refused outright - archive entry names are
        // data we did not write (the "zip slip" class).
        var escape = await http.GetAsync(
            $"{SrcHubBaseUrl}/src?url={Uri.EscapeDataString(archive)}&member={Uri.EscapeDataString("../escape")}");
        if (escape.StatusCode != HttpStatusCode.BadRequest)
            throw new Exception($"expected 400 for a traversing member path, got {(int)escape.StatusCode}");

        Console.WriteLine($"[SourceProxy] archive has {count} members; extracted {bytes.Length} bytes; "
                        + "traversal refused");
    }

    [TestMethod(Timeout = 300000)]
    public async Task SourceProxy_CacheIsBoundedSoTheDriveCannotFill()
    {
        using var http = await RequireSourceProxyAsync();
        var probe = await http.GetAsync($"{SrcHubBaseUrl}/src/stats");
        if (probe.StatusCode == HttpStatusCode.NotFound)
            throw new UnsupportedTestException(
                "the deployed hub predates cache eviction - redeploy to arm this");
        probe.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        long cache = root.GetProperty("cacheBytes").GetInt64();
        long free = root.GetProperty("freeBytes").GetInt64();
        long minFree = root.GetProperty("minFreeBytes").GetInt64();
        long maxCache = root.GetProperty("maxCacheBytes").GetInt64();

        const double GB = 1024d * 1024 * 1024;
        Console.WriteLine($"[SourceProxy] cache {cache / GB:F2} GB / cap {maxCache / GB:F2} GB, "
                        + $"free {free / GB:F2} GB / floor {minFree / GB:F2} GB");

        // An unconfigured floor is the whole failure this guards against: the proxy caches whole archives,
        // so an unbounded cache grows until the drive is full and then the proxy does not degrade, it FAILS
        // - taking the tracker and web seed on the same disk with it.
        if (minFree <= 0)
            throw new Exception("the hub reports NO free-space floor - its cache is unbounded and the drive "
                              + "can fill. Set SourceProxy:MinFreeDiskSpaceBytes.");

        // The live invariant. If eviction ever stops working, the deployed cache grows past its cap and this
        // goes red on the real server - which is the only place the bug would ever actually matter.
        if (maxCache > 0 && cache > maxCache)
            throw new Exception($"cache is {cache} bytes, OVER its {maxCache}-byte cap - eviction is not "
                              + "keeping up, and the drive is filling right now");

        if (free < minFree)
            throw new Exception($"only {free / GB:F2} GB free, under the {minFree / GB:F2} GB floor - the "
                              + "proxy is about to start refusing fetches");
    }

    [TestMethod(Timeout = 600000)]
    public async Task SourceProxy_ACachedFileIsServedAgainRatherThanRefused()
    {
        using var http = await RequireSourceProxyAsync();
        var url = $"{SrcHubBaseUrl}/src?url={Uri.EscapeDataString(SrcSampleUrl)}";

        // Regression guard for two bugs that only appear once a cache has a LIMIT, both found by running it:
        //
        //  1. Budgeting a warm hit as if its whole size were arriving counts those bytes twice - once on
        //     disk, once "incoming" - so a file that needs no space at all gets refused with a 507.
        //  2. Evicting a file without discarding the reader that memoised its state leaves that reader
        //     convinced it still holds a complete file, and the NEXT request 500s reading bytes that are gone.
        //
        // Both show up here as a second identical request behaving differently from the first, which is
        // exactly the thing a cache must never do.
        var first = await http.GetAsync(url);
        first.EnsureSuccessStatusCode();
        var a = await first.Content.ReadAsByteArrayAsync();

        for (int i = 0; i < 3; i++)
        {
            var again = await http.GetAsync(url);
            if (again.StatusCode == HttpStatusCode.InsufficientStorage)
                throw new Exception("a file already in the cache was refused for space (507) - a warm hit "
                                  + "consumes no additional disk and must never be refused");
            if (!again.IsSuccessStatusCode)
                throw new Exception($"repeat request {i + 2} returned {(int)again.StatusCode}; a cached file "
                                  + "must serve identically every time");
            var b = await again.Content.ReadAsByteArrayAsync();
            if (!a.SequenceEqual(b))
                throw new Exception($"repeat request {i + 2} returned different bytes ({b.Length} vs {a.Length})");
        }
        Console.WriteLine($"[SourceProxy] {a.Length} bytes served identically across 4 requests");
    }

    [TestMethod(Timeout = 300000)]
    public async Task SourceProxy_WarmsALargeArchiveWithoutHoldingTheRequestOpen()
    {
        using var http = await RequireSourceProxyAsync();

        // ⚠️ This is the bug this endpoint exists for, and it is worth stating precisely because the symptom
        // points at the wrong machine: fetching the 634 MB fp32 ZipVoice archive through the blocking path
        // returned 504 after 25 SECONDS - the gateway's timeout - while the hub was still fetching happily.
        // "The hub is broken" and "the gateway gave up on the hub" look identical from the client.
        //
        // Raising that timeout only moves the cliff; the next archive is bigger. So the contract asserted
        // here is that warming NEVER blocks: 202 straight away, 200 once cached, and the caller waits
        // between requests rather than inside one.
        const string bigArchive =
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/sherpa-onnx-zipvoice-distill-zh-en-emilia.tar.bz2";

        var probe = await http.GetAsync($"{SrcHubBaseUrl}/src/warm?url={Uri.EscapeDataString(bigArchive)}");
        if (probe.StatusCode == HttpStatusCode.NotFound)
            throw new UnsupportedTestException(
                "the deployed hub has no /src/warm yet - redeploy to arm this");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await http.GetAsync($"{SrcHubBaseUrl}/src/warm?url={Uri.EscapeDataString(bigArchive)}");
        sw.Stop();

        if (res.StatusCode != HttpStatusCode.Accepted && res.StatusCode != HttpStatusCode.OK)
            throw new Exception($"warm returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");

        // The whole point is that this returns without waiting for a 634 MB download. Twenty seconds is
        // generous and still comfortably under the 25s gateway timeout that made the blocking path fail.
        if (sw.Elapsed > TimeSpan.FromSeconds(20))
            throw new Exception($"warm took {sw.Elapsed.TotalSeconds:F1}s - it must return immediately, "
                              + "or it is just the blocking fetch again and the gateway will kill it");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("cached", out var cached))
            throw new Exception("warm did not report a 'cached' state, so a caller cannot tell when to proceed");

        if (res.StatusCode == HttpStatusCode.Accepted)
        {
            // 202 must mean "not ready", or polling on the status code alone - which is the point of
            // separating the codes - would tell the caller to proceed before the bytes exist.
            if (cached.GetBoolean())
                throw new Exception("warm answered 202 Accepted while reporting cached=true; the status "
                                  + "code and the body disagree and a polling caller trusts the code");
        }
        else if (!cached.GetBoolean())
        {
            throw new Exception("warm answered 200 OK while reporting cached=false");
        }

        Console.WriteLine($"[SourceProxy] warm answered {(int)res.StatusCode} in {sw.Elapsed.TotalSeconds:F2}s "
                        + $"(cached={cached.GetBoolean()})");
    }
}
