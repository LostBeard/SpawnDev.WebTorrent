using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using SpawnDev.UnitTesting;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Live integration for the hub's DEPRECATED model endpoints (<c>/hf</c>, <c>/ollama</c>, <c>/src</c>), which now answer
/// 307 to https://models.spawndev.com when the file is mirrored there and fall through to the old proxy when it is not.
/// Real hub, real models host, no mocks.
///
/// <para>
/// ⚠️ The REDIRECT SHAPE (307, Location) can only be seen with auto-redirect off, which a browser's fetch cannot do, so
/// those tests are desktop-only. The browser lane runs the end-to-end one: a cross-origin ranged request through the
/// redirect must still deliver the bytes with a readable <c>Content-Range</c>.
/// </para>
/// <para>
/// Desktop runs can aim at a local hub (<c>SPAWNDEV_HUB_URL=http://127.0.0.1:5591</c>) to prove a build before it is
/// deployed. Otherwise they hit the live hub and SKIP, naming the reason, while the deployed hub predates the redirect.
/// </para>
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private const string LiveHubBaseUrl = "https://hub.spawndev.com:44365";
    private const string ModelsHostBaseUrl = "https://models.spawndev.com";

    private static string RedirectHubBase()
        => (OperatingSystem.IsBrowser() ? null : Environment.GetEnvironmentVariable("SPAWNDEV_HUB_URL")) ?? LiveHubBaseUrl;

    /// <summary>A normal client (follows redirects) after checking the hub has the redirect feature.</summary>
    private static async Task<HttpClient> RequireModelsRedirectAsync()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        HttpResponseMessage probe;
        try { probe = await http.GetAsync($"{RedirectHubBase()}/deprecated-stats"); }
        catch (Exception ex) { throw new UnsupportedTestException($"hub unreachable ({ex.GetType().Name}) - needs internet"); }
        if (probe.StatusCode == HttpStatusCode.NotFound)
            throw new UnsupportedTestException(
                "the deployed hub predates ModelsRedirect (/deprecated-stats is 404) - deploy it, or set SPAWNDEV_HUB_URL to a local build");
        probe.EnsureSuccessStatusCode();
        return http;
    }

    /// <summary>A client that does NOT follow redirects, so the 307 itself can be read. Desktop only.</summary>
    [UnsupportedOSPlatform("browser")]
    private static HttpClient NoRedirectClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };

    [UnsupportedOSPlatform("browser")]
    private static async Task<HttpResponseMessage> RedirectProbeAsync(HttpClient http, HttpMethod method, string pathAndQuery)
    {
        var req = new HttpRequestMessage(method, RedirectHubBase() + pathAndQuery);
        req.Headers.TryAddWithoutValidation("Origin", "https://example.com");
        return await http.SendAsync(req);
    }

    private static void ExpectRedirect(HttpResponseMessage res, string expectedLocation, string what)
    {
        if (res.StatusCode != HttpStatusCode.TemporaryRedirect)
            throw new Exception($"{what}: expected 307, got {(int)res.StatusCode} {res.StatusCode}");
        var loc = res.Headers.Location?.OriginalString;
        if (loc != expectedLocation)
            throw new Exception($"{what}: Location was\n  {loc}\nexpected\n  {expectedLocation}");
        if (!res.Headers.TryGetValues("Deprecation", out var dep) || dep.First() != "true")
            throw new Exception($"{what}: missing 'Deprecation: true' header");
        // A browser rejects a cross-origin redirect whose OWN response lacks Allow-Origin, so the 307 must carry it.
        if (!res.Headers.TryGetValues("Access-Control-Allow-Origin", out _))
            throw new Exception($"{what}: the 307 has no Access-Control-Allow-Origin, a browser would refuse it");
        if (res.Headers.CacheControl?.MaxAge is not { } age || age < TimeSpan.FromMinutes(1))
            throw new Exception($"{what}: the 307 is not cacheable (a model download is thousands of Range requests)");
    }

    [TestMethod(Timeout = 300000)]
    public async Task ModelsRedirect_HfPathRedirectsToModelsHost_Desktop()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("reading a 307 needs auto-redirect off, which fetch cannot do");
        using var _ = await RequireModelsRedirectAsync();
        using var http = NoRedirectClient();
        const string expected = ModelsHostBaseUrl + "/LostBeard/spawnscene-models/resolve/main/README.md";
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var res = await RedirectProbeAsync(http, method, "/hf/LostBeard/spawnscene-models/README.md");
            ExpectRedirect(res, expected, $"{method} /hf");
        }
        // ...and the Location really serves the file (a redirect to a 404 would be worse than no redirect).
        using var target = await new HttpClient().GetAsync(expected);
        if (!target.IsSuccessStatusCode) throw new Exception($"the redirect target answered {(int)target.StatusCode}");
        Console.WriteLine("[ModelsRedirect] /hf GET+HEAD -> 307 -> models host, target serves 200");
    }

    [TestMethod(Timeout = 300000)]
    public async Task ModelsRedirect_OllamaAndSrcPathsMapToTheirModelsHostKeys_Desktop()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("reading a 307 needs auto-redirect off, which fetch cannot do");
        using var _ = await RequireModelsRedirectAsync();
        using var http = NoRedirectClient();

        // Ollama: the route says {layer}; the mirrored file is named {model}-{tag}-{layer}.gguf, as the hub cached it.
        using (var res = await RedirectProbeAsync(http, HttpMethod.Get, "/ollama/gemma3/4b/license"))
            ExpectRedirect(res, ModelsHostBaseUrl + "/ollama/gemma3/4b/gemma3-4b-license.gguf", "/ollama license");

        // Source proxy, a Hugging Face DATASET url.
        var ds = "https://huggingface.co/datasets/LostBeard/spawnscene-samples/resolve/main/pinecone/001.jpg";
        using (var res = await RedirectProbeAsync(http, HttpMethod.Get, $"/src?url={Uri.EscapeDataString(ds)}"))
            ExpectRedirect(res, ModelsHostBaseUrl + "/datasets/LostBeard/spawnscene-samples/resolve/main/pinecone/001.jpg", "/src dataset");

        // Source proxy, a member of a GitHub archive: the archive cannot be range-read, so its members were stored
        // as plain files under the archive's URL minus the extension. A name with a space and '!' must survive.
        var archive = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/sherpa-onnx-zipvoice-distill-int8-zh-en-emilia.tar.bz2";
        var member = "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia/espeak-ng-data/voices/!v/Mr serious";
        using (var res = await RedirectProbeAsync(http, HttpMethod.Get,
                   $"/src?url={Uri.EscapeDataString(archive)}&member={Uri.EscapeDataString(member)}"))
            ExpectRedirect(res, ModelsHostBaseUrl + "/github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/"
                + "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia/espeak-ng-data/voices/%21v/Mr%20serious", "/src archive member");
        Console.WriteLine("[ModelsRedirect] ollama + src (dataset, archive member with space and '!') map to the right keys");
    }

    [TestMethod(Timeout = 300000)]
    public async Task ModelsRedirect_FilesNotMirroredFallThroughToTheOldProxy_Desktop()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("reading a 307 needs auto-redirect off, which fetch cannot do");
        using var _ = await RequireModelsRedirectAsync();
        using var http = NoRedirectClient();
        // A path that cannot exist on the models host. It must NOT redirect (a redirect would turn a working file into
        // a 404), it must reach the old proxy, and it is still counted and marked deprecated.
        using var res = await RedirectProbeAsync(http, HttpMethod.Get, "/hf/LostBeard/spawnscene-models/definitely-not-mirrored-xyz.bin");
        if (res.StatusCode == HttpStatusCode.TemporaryRedirect)
            throw new Exception("redirected a file that is not on the models host");
        if (!res.Headers.TryGetValues("Deprecation", out var dep) || dep.First() != "true")
            throw new Exception("the fall-through response lacks 'Deprecation: true'");
        // The old proxy asked Hugging Face, which said the file does not exist. That is a 404 for the client; a 502 says
        // "the origin is unreachable", which sends callers off retrying a file that will never appear.
        if (res.StatusCode != HttpStatusCode.NotFound)
            throw new Exception($"a file that does not exist upstream must be 404, got {(int)res.StatusCode} {res.StatusCode}");
        Console.WriteLine("[ModelsRedirect] unmirrored nonexistent file fell through to the proxy: 404, marked Deprecation");
    }

    [TestMethod(Timeout = 300000)]
    public async Task ModelsRedirect_StatsCountEveryDeprecatedHit_Desktop()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("needs auto-redirect off");
        using var normal = await RequireModelsRedirectAsync();
        using var http = NoRedirectClient();

        async Task<long> CountAsync(string key)
        {
            using var doc = JsonDocument.Parse(await normal.GetStringAsync($"{RedirectHubBase()}/deprecated-stats"));
            return doc.RootElement.GetProperty("byEndpointAndOutcome").TryGetProperty(key, out var v) ? v.GetInt64() : 0;
        }

        var before = await CountAsync("hf redirected");
        for (var i = 0; i < 3; i++)
            using (await RedirectProbeAsync(http, HttpMethod.Get, "/hf/LostBeard/spawnscene-models/README.md")) { }
        var after = await CountAsync("hf redirected");
        if (after - before != 3)
            throw new Exception($"/deprecated-stats 'hf redirected' moved by {after - before}, expected 3 (before {before}, after {after})");
        Console.WriteLine($"[ModelsRedirect] stats counted 3 redirected hits ({before} -> {after})");
    }

    [TestMethod(Timeout = 300000)]
    public async Task ModelsHost_PreflightAllowsEveryHeaderTheDownloaderSends_Desktop()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("a browser sends the preflight itself; this asks for it directly");
        // The headers a model downloader may put on a cross-origin GET. R2 answers 403 with NO Allow-Origin to a
        // preflight that names a header outside its configured list, which a browser reports as a CORS failure.
        const string headers = "range, if-range, if-none-match, cache-control, pragma, content-type";
        // SPAWNDEV_MODELS_URL aims the direct check at another host (a throwaway bucket with the OLD, too-narrow CORS
        // rule is how this test was red-checked without re-breaking the live site).
        var modelsHost = Environment.GetEnvironmentVariable("SPAWNDEV_MODELS_URL") ?? ModelsHostBaseUrl;
        foreach (var target in new[] { modelsHost + "/LostBeard/spawnscene-models/resolve/main/README.md", null })
        {
            var url = target ?? $"{RedirectHubBase()}/hf/LostBeard/spawnscene-models/README.md";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
            var req = new HttpRequestMessage(HttpMethod.Options, url);
            req.Headers.TryAddWithoutValidation("Origin", "https://lostbeard.github.io");
            req.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
            req.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", headers);
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                throw new Exception($"preflight to {url} answered {(int)res.StatusCode}: a browser would block the request");
            if (!res.Headers.TryGetValues("Access-Control-Allow-Origin", out var origin) || origin.First() is not ("*" or "https://lostbeard.github.io"))
                throw new Exception($"preflight to {url} has no usable Access-Control-Allow-Origin");
            if (!res.Headers.TryGetValues("Access-Control-Allow-Headers", out var allowed)
                || !headers.Split(", ").All(h => allowed.First().Contains(h, StringComparison.OrdinalIgnoreCase) || allowed.First() == "*"))
                throw new Exception($"preflight to {url} did not allow every requested header; allowed: {allowed?.FirstOrDefault()}");
        }
        Console.WriteLine("[ModelsRedirect] preflights to the models host and to the hub allow Range, If-Range and the other downloader headers");
    }

    [TestMethod(Timeout = 300000)]
    public async Task ModelsHost_LargeFilesAreNeverEdgeCachedSoTheFirstRangeProbeIs206_Desktop()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("cf-cache-status is not readable from a browser");
        // 🔴 With a Cache Everything rule, the FIRST request for a file too big to cache (over ~512 MB) answered 200 with
        // the WHOLE body, ignoring Range; every later request answered a correct 206. HttpModelDownloader sends a bounded
        // Range probe and only goes parallel/segmented on a 206, so the first download of every multi-GB safetensors/gguf
        // streamed over ONE connection (TJ saw exactly that, 2026-10-20). The first request is the only bad one, so a
        // re-run can never see the bug; the stable signal is that the host never makes these files cache-eligible:
        // cf-cache-status DYNAMIC. A rule that makes them eligible changes it to BYPASS/MISS/HIT.
        var paths = Environment.GetEnvironmentVariable("SPAWNDEV_LARGE_FILE_PATH") is { Length: > 0 } one
            ? new[] { one }
            : new[]
            {
                "/LostBeard/FLUX.2-klein-4B-q4-ILGPU/resolve/main/transformer_q4.safetensors",
                "/LostBeard/FLUX.2-klein-4B-q4-ILGPU/resolve/main/text_encoder_q4.safetensors",
                "/ollama/gemma4/12b/gemma4-12b-model.gguf",
            };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        foreach (var path in paths)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, ModelsHostBaseUrl + path);
            req.Headers.TryAddWithoutValidation("Origin", "https://lostbeard.github.io");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 524287);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (res.StatusCode != HttpStatusCode.PartialContent)
                throw new Exception($"{path}: a bounded Range probe answered {(int)res.StatusCode}, not 206: the downloader would stream the whole file over one connection");
            res.Headers.TryGetValues("cf-cache-status", out var status);
            if (status?.FirstOrDefault() != "DYNAMIC")
                throw new Exception($"{path}: cf-cache-status is '{status?.FirstOrDefault()}', expected DYNAMIC. A cache rule makes the first request for a big file answer 200 with the whole body.");
        }
        Console.WriteLine($"[ModelsHost] {paths.Length} large file(s): Range probe 206, never cache-eligible (cf-cache-status DYNAMIC)");
    }

    [TestMethod(Timeout = 600000)]
    public async Task ModelsRedirect_FollowedRedirectDeliversRangeBytesWithReadableContentRange()
    {
        using var http = await RequireModelsRedirectAsync();
        // The real client flow, in the browser too: a cross-origin ranged request to the OLD hub URL must come out the
        // other side of the redirect as a 206 whose Content-Range a browser is allowed to read.
        var url = $"{RedirectHubBase()}/hf/LostBeard/spawnscene-models/migan_float.onnx";

        // ⚠️ Send what the real downloader sends: HttpModelDownloader adds If-Range = the known ETag to every segment.
        // An If-Range header makes the cross-origin request carry it in the CORS PREFLIGHT, and the models host
        // answered 403 (no Allow-Origin) to a preflight naming any header outside its allow list: the live demo's
        // image generator died on 2026-10-20 while a Range-only test passed. The ETag is readable cross-origin
        // because the models host exposes it.
        string? etag;
        using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url)))
        {
            head.EnsureSuccessStatusCode();
            etag = head.Headers.ETag?.ToString();
        }
        if (string.IsNullOrEmpty(etag)) throw new Exception("no readable ETag on the HEAD response through the redirect");

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
        req.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(
            new System.Net.Http.Headers.EntityTagHeaderValue(etag.TrimStart('W', '/')));
        using var res = await http.SendAsync(req);
        if (res.StatusCode != HttpStatusCode.PartialContent)
            throw new Exception($"expected 206 through the redirect with Range + If-Range, got {(int)res.StatusCode}");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (bytes.Length != 100) throw new Exception($"expected 100 bytes, got {bytes.Length}");
        var total = res.Content.Headers.ContentRange?.Length;
        if (total != 29546882)
            throw new Exception($"Content-Range total was {total}, expected 29546882 (migan_float.onnx)");
        Console.WriteLine("[ModelsRedirect] old /hf URL + Range -> 206, 100 bytes, Content-Range total 29546882");
    }
}
