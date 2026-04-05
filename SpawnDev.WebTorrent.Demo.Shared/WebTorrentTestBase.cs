using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Abstract base class for shared WebTorrent tests.
/// Runs identically in browser (Blazor WASM via UnitTestsView) and desktop (via ConsoleRunner).
/// Concrete subclasses provide the WebTorrentClient via DI constructor.
/// All tests use [TestMethod] from SpawnDev.UnitTesting — real data, real hashing, no mocks.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    protected WebTorrentClient Client { get; }

    protected WebTorrentTestBase(WebTorrentClient client)
    {
        Client = client;
    }

    /// <summary>Generate deterministic test data with a seeded Random.</summary>
    protected static byte[] MakeDeterministicData(int size, int seed = 42)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Create a fresh client for isolated tests (no OPFS, no peer factory).</summary>
    protected static WebTorrentClient CreateIsolatedClient()
    {
        return new WebTorrentClient();
    }
}
