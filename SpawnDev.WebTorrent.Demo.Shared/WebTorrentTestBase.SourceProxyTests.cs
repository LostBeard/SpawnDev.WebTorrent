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

        if (!full.Headers.TryGetValues("Access-Control-Allow-Origin", out _))
            throw new Exception("no Access-Control-Allow-Origin - a browser could not read this at all");
        // ⚠️ The one that gets forgotten: without it a cross-origin caller receives a 206 it cannot
        // interpret, because Content-Range is invisible to it. That looks like a broken server.
        if (!full.Headers.TryGetValues("Access-Control-Expose-Headers", out var expose)
            || !string.Join(",", expose).Contains("Content-Range", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Content-Range is not exposed - a seeking reader cannot use this response");

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 15);
        var partial = await http.SendAsync(req);
        if (partial.StatusCode != HttpStatusCode.PartialContent)
            throw new Exception($"expected 206 for a Range request, got {(int)partial.StatusCode} - "
                              + "the lazy-hash streamer seeks, so a proxy that ignores Range is unusable");
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

        var listJson = await http.GetStringAsync($"{SrcHubBaseUrl}/src/list?url={Uri.EscapeDataString(archive)}");
        using var doc = JsonDocument.Parse(listJson);
        var count = doc.RootElement.GetProperty("entries").GetArrayLength();
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
}
