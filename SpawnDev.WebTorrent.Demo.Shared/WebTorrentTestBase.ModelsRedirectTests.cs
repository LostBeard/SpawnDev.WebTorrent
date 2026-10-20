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

    [TestMethod(Timeout = 600000)]
    public async Task ModelsRedirect_FollowedRedirectDeliversRangeBytesWithReadableContentRange()
    {
        using var http = await RequireModelsRedirectAsync();
        // The real client flow, in the browser too: a cross-origin ranged request to the OLD hub URL must come out the
        // other side of the redirect as a 206 whose Content-Range a browser is allowed to read.
        var req = new HttpRequestMessage(HttpMethod.Get, $"{RedirectHubBase()}/hf/LostBeard/spawnscene-models/migan_float.onnx");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
        using var res = await http.SendAsync(req);
        if (res.StatusCode != HttpStatusCode.PartialContent)
            throw new Exception($"expected 206 through the redirect, got {(int)res.StatusCode}");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (bytes.Length != 100) throw new Exception($"expected 100 bytes, got {bytes.Length}");
        var total = res.Content.Headers.ContentRange?.Length;
        if (total != 29546882)
            throw new Exception($"Content-Range total was {total}, expected 29546882 (migan_float.onnx)");
        Console.WriteLine("[ModelsRedirect] old /hf URL + Range -> 206, 100 bytes, Content-Range total 29546882");
    }
}
