using SpawnDev.WebTorrent;
using System.Diagnostics;

namespace PlaywrightMultiTest;

/// <summary>
/// Tests C# WebTorrent client against a real JS WebTorrent seeder.
/// Starts a Node.js process that seeds deterministic data via WebRTC,
/// then our C# client connects and downloads it. Verifies byte-for-byte
/// data integrity across the C#/JS boundary.
///
/// Requires: Node.js + npm install webtorrent (in js-interop/)
/// Disabled by default - enable for interop verification.
/// </summary>
[TestFixture]
public class JsInteropTest
{
    private const int DataSize = 32768;
    private const int PieceLength = 16384;
    private const string ScriptPath = "js-interop/seed-test.mjs";

    /// <summary>Generate the same deterministic data the JS seeder uses.</summary>
    private static byte[] GenerateTestData()
    {
        var data = new byte[DataSize];
        for (int i = 0; i < DataSize; i++) data[i] = (byte)((i * 7 + 13) % 256);
        return data;
    }

    [Test, Timeout(120_000)]
    [Category("Interop")]
    public async Task CSharp_Downloads_From_JsWebTorrent()
    {
        // Check if Node.js is available
        var nodeCheck = Process.Start(new ProcessStartInfo("node", "--version")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        });
        nodeCheck?.WaitForExit(5000);
        if (nodeCheck?.ExitCode != 0)
        {
            Assert.Ignore("Node.js not available");
            return;
        }

        // Check if webtorrent is installed
        var scriptDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ScriptPath);
        if (!File.Exists(scriptDir))
            scriptDir = Path.Combine(Directory.GetCurrentDirectory(), ScriptPath);
        if (!File.Exists(scriptDir))
        {
            // Try relative to project
            scriptDir = ScriptPath;
        }

        Console.WriteLine($"[Interop] Starting JS WebTorrent seeder: {scriptDir}");
        WebTorrentClient.VerboseLogging = true;

        // Start JS seeder - it outputs magnet URI on stdout
        var jsProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = scriptDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(scriptDir)) ?? ".",
            }
        };

        jsProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine(e.Data);
        };

        jsProcess.Start();
        jsProcess.BeginErrorReadLine();

        // Read magnet URI from JS process stdout
        var magnetUri = await jsProcess.StandardOutput.ReadLineAsync();
        Console.WriteLine($"[Interop] JS seeder magnet: {magnetUri}");

        if (string.IsNullOrEmpty(magnetUri) || !magnetUri.StartsWith("magnet:"))
        {
            jsProcess.Kill();
            Assert.Fail("JS seeder did not output a valid magnet URI");
            return;
        }

        // Give JS seeder time to announce to tracker
        await Task.Delay(3000);

        // Create C# client and download
        var client = new WebTorrentClient();
        client.PeerFactory = (initiator) => new RtcPeer(initiator, trickle: false);
        var torrent = client.Add(magnetUri);

        Console.WriteLine($"[Interop] C# client added torrent: {torrent.InfoHashHex}");

        // Wait for metadata
        var metaTimeout = DateTime.UtcNow.AddSeconds(60);
        while (!torrent.HasMetadata && DateTime.UtcNow < metaTimeout)
            await Task.Delay(500);

        if (!torrent.HasMetadata)
        {
            jsProcess.Kill();
            await client.DisposeAsync();
            Assert.Fail($"No metadata after 60s. Peers={torrent.PeerCount}");
            return;
        }

        Console.WriteLine($"[Interop] Metadata received: {torrent.Name}, {torrent.PieceCount} pieces");

        // Wait for download to complete
        var dlTimeout = DateTime.UtcNow.AddSeconds(60);
        while (!torrent.Done && DateTime.UtcNow < dlTimeout)
            await Task.Delay(500);

        Console.WriteLine($"[Interop] Download: Done={torrent.Done}, Downloaded={torrent.Downloaded}, Peers={torrent.PeerCount}");

        // Verify data
        byte[]? downloadedData = null;
        if (torrent.Done && torrent.Files?.Length > 0)
        {
            downloadedData = await torrent.ReadFileAsync(0);
        }

        // Cleanup
        jsProcess.Kill();
        await client.DisposeAsync();
        WebTorrentClient.VerboseLogging = false;

        // Assertions
        Assert.That(torrent.Done, Is.True, "Torrent should be fully downloaded");
        Assert.That(downloadedData, Is.Not.Null, "Should be able to read the file");

        var expectedData = GenerateTestData();
        Assert.That(downloadedData!.Length, Is.EqualTo(expectedData.Length),
            $"Downloaded {downloadedData.Length} bytes, expected {expectedData.Length}");
        Assert.That(downloadedData, Is.EqualTo(expectedData),
            "Downloaded data must match the JS seeder's data byte-for-byte");

        Console.WriteLine($"[Interop] SUCCESS: C# downloaded {downloadedData.Length} bytes from JS WebTorrent, verified byte-for-byte");
    }
}
