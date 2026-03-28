using SpawnDev.BlazorJS;
using SpawnDev.WebTorrent.Demo.Shared.UnitTests;

namespace SpawnDev.WebTorrent.Demo.UnitTests;

/// <summary>
/// Browser-side tests. Inherits all shared tests from WebTorrentTestBase.
/// Injected via DI (registered as singleton in Program.cs).
/// </summary>
public class BrowserTests : WebTorrentTestBase
{
    public BrowserTests(BlazorJSRuntime js)
    {
        JS = js;
    }
}
