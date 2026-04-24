using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Demo.Shared;

namespace SpawnDev.WebTorrent.WpfDemo;

/// <summary>
/// WPF concrete subclass of <see cref="WebTorrentTestBase"/>. Inherits every
/// [TestMethod] so the WPF client's "Tests" button runs the full shared suite
/// (same tests as DemoConsole + browser demo, 480+ and growing).
/// </summary>
public class DesktopTests : WebTorrentTestBase
{
    public DesktopTests(WebTorrentClient client) : base(client) { }
}
