using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Demo.Shared;

namespace SpawnDev.WebTorrent.Demo;

/// <summary>
/// Browser concrete subclass of WebTorrentTestBase.
/// Inherits all 86 [TestMethod] tests. Registered as a singleton in Program.cs.
/// Discovered by Tests.razor UnitTestsView through DI service enumeration.
/// </summary>
public class BrowserTests : WebTorrentTestBase
{
    public BrowserTests(WebTorrentClient client) : base(client) { }
}
