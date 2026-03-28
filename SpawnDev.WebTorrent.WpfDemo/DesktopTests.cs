using SpawnDev.WebTorrent.Demo.Shared.UnitTests;

namespace SpawnDev.WebTorrent.WpfDemo;

/// <summary>
/// Desktop-side tests. Inherits all shared tests from WebTorrentTestBase.
/// Browser-only tests (OPFS, WebRTC) auto-skip via OperatingSystem.IsBrowser() checks.
/// </summary>
public class DesktopTests : WebTorrentTestBase
{
}
