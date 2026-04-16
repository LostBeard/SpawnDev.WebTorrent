using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Demo.Shared;

namespace SpawnDev.WebTorrent.DemoConsole;

/// <summary>
/// Desktop concrete subclass of WebTorrentTestBase.
/// Inherits all 86 [TestMethod] tests. No overrides needed.
/// OPFS tests self-skip via UnsupportedTestException. Network tests use RtcPeer (SpawnDev.RTC).
/// </summary>
public class DesktopTests : WebTorrentTestBase
{
    public DesktopTests(WebTorrentClient client) : base(client) { }
}
