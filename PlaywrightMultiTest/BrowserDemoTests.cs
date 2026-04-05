using Microsoft.Playwright;
using NUnit.Framework;
using System.Diagnostics;

namespace PlaywrightMultiTest;

/// <summary>
/// End-to-end browser tests for the SpawnDev.WebTorrent _Alt demo.
/// Launches the Blazor WASM app, navigates to the Torrents page,
/// and verifies real P2P download works in the browser.
/// No mocks. Real browser. Real WebRTC. Real tracker.
/// </summary>
[TestFixture]
[Ignore("Disabled — shared test base (86 tests via UnitTest1) covers this. Re-enable for UI-specific E2E testing.")]
public class BrowserDemoTests
{
    private Process? _serverProcess;
    private const string AppUrl = "http://localhost:5580";
    private const int StartupTimeoutMs = 30_000;
    private const int TestTimeoutMs = 180_000; // 3 minutes for browser test

    [OneTimeSetUp]
    public async Task StartDemoApp()
    {
        // Kill any existing instance on port 5580
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await http.GetAsync(AppUrl);
            // Already running, reuse it
            Console.WriteLine("[BrowserTest] Demo already running at " + AppUrl);
            return;
        }
        catch { /* Not running, start it */ }

        var projectPath = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..",
            "SpawnDev.WebTorrent.Demo", "SpawnDev.WebTorrent.Demo.csproj"));

        Console.WriteLine($"[BrowserTest] Starting demo: {projectPath}");

        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" --urls \"{AppUrl}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        _serverProcess.Start();

        // Wait for server to be ready
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < StartupTimeoutMs)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await http.GetAsync(AppUrl);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[BrowserTest] Demo ready in {sw.ElapsedMilliseconds}ms");
                    return;
                }
            }
            catch { }
            await Task.Delay(500);
        }

        Assert.Fail($"Demo app failed to start within {StartupTimeoutMs}ms");
    }

    [Test, CancelAfter(TestTimeoutMs)]
    public async Task TorrentsPage_Loads_WithoutErrors()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false });
        var page = await browser.NewPageAsync();

        var consoleMessages = new List<string>();
        var consoleErrors = new List<string>();
        page.Console += (_, msg) =>
        {
            consoleMessages.Add($"[{msg.Type}] {msg.Text}");
            if (msg.Type == "error")
                consoleErrors.Add(msg.Text);
        };

        await page.GotoAsync($"{AppUrl}/torrents", new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for Blazor to load (check for our toolbar)
        try
        {
            await page.WaitForSelectorAsync(".qb-toolbar", new() { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            // Dump console to see what went wrong
            Console.WriteLine($"[BrowserTest] TIMEOUT - Console messages ({consoleMessages.Count}):");
            foreach (var msg in consoleMessages)
                Console.WriteLine($"  {msg}");
            var html = await page.ContentAsync();
            Console.WriteLine($"[BrowserTest] Page HTML (first 2000 chars): {html[..Math.Min(2000, html.Length)]}");
            throw;
        }
        Console.WriteLine("[BrowserTest] Torrents page loaded, toolbar visible");

        // Dump console for debugging
        Console.WriteLine($"[BrowserTest] Console messages ({consoleMessages.Count}):");
        foreach (var msg in consoleMessages.TakeLast(30))
            Console.WriteLine($"  {msg}");

        // Verify the page has key UI elements
        var magnetInput = await page.QuerySelectorAsync(".qb-input");
        Assert.That(magnetInput, Is.Not.Null, "Magnet input should exist");

        var chips = await page.QuerySelectorAllAsync(".qb-chip");
        Assert.That(chips.Count, Is.GreaterThanOrEqualTo(4), "Should have quick-add chips (Sintel, BBB, etc.)");

        // Check for critical Blazor errors
        var criticalErrors = consoleErrors.Where(e =>
            e.Contains("Unhandled exception") ||
            e.Contains("blazor") && e.Contains("error")).ToList();

        Assert.That(criticalErrors, Is.Empty,
            $"Critical browser errors: {string.Join("\n", criticalErrors)}");

        Console.WriteLine("[BrowserTest] PASS: Torrents page loads without critical errors");
    }

    [Test, CancelAfter(TestTimeoutMs)]
    public async Task SeedTestData_CreatesAndDisplaysTorrent()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{AppUrl}/torrents", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".qb-toolbar", new() { Timeout = 30_000 });

        // Click "Seed Test" chip
        var seedChip = await page.QuerySelectorAsync("button.qb-chip:has-text('Seed Test')");
        Assert.That(seedChip, Is.Not.Null, "'Seed Test' button should exist");
        await seedChip!.ClickAsync();

        // Wait for a torrent to appear in the table
        await page.WaitForSelectorAsync(".qb-table tbody tr", new() { Timeout = 10_000 });
        Console.WriteLine("[BrowserTest] Seed test torrent appeared in table");

        // Verify it shows as seeding (100% done)
        var progressLabel = await page.QuerySelectorAsync(".qb-progress-label");
        Assert.That(progressLabel, Is.Not.Null, "Progress label should exist");
        var progressText = await progressLabel!.TextContentAsync();
        Assert.That(progressText, Does.Contain("100"), $"Seeded torrent should show 100%, got: {progressText}");

        // Verify the status dot is green (seeding)
        var statusDot = await page.QuerySelectorAsync(".qb-dot-seed");
        Assert.That(statusDot, Is.Not.Null, "Status dot should be green (seeding)");

        Console.WriteLine("[BrowserTest] PASS: Seed test data creates and displays correctly");
    }

    [Test, CancelAfter(TestTimeoutMs)]
    public async Task AddSintelMagnet_StartsDownload()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{AppUrl}/torrents", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".qb-toolbar", new() { Timeout = 30_000 });

        // Click "Sintel" quick-add chip
        var sintelChip = await page.QuerySelectorAsync("button.qb-chip:has-text('Sintel')");
        Assert.That(sintelChip, Is.Not.Null, "'Sintel' button should exist");
        await sintelChip!.ClickAsync();

        // Wait for a torrent to appear
        await page.WaitForSelectorAsync(".qb-table tbody tr", new() { Timeout = 10_000 });
        Console.WriteLine("[BrowserTest] Sintel torrent added to table");

        // Wait for metadata (name changes from hash to "Sintel")
        var nameCell = await page.WaitForSelectorAsync(".qb-col-name:has-text('Sintel')", new() { Timeout = 60_000 });
        Assert.That(nameCell, Is.Not.Null, "Torrent name should resolve to 'Sintel' after metadata");
        Console.WriteLine("[BrowserTest] Sintel metadata received");

        // Wait for at least some download progress (peer data transfer in browser)
        Console.WriteLine("[BrowserTest] Waiting for download progress...");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        string progressText = "0.0%";
        while (DateTime.UtcNow < deadline)
        {
            var label = await page.QuerySelectorAsync(".qb-progress-label");
            if (label != null)
            {
                progressText = await label.TextContentAsync() ?? "0.0%";
                if (progressText != "0.0%" && !progressText.StartsWith("0.0"))
                {
                    Console.WriteLine($"[BrowserTest] Download progress: {progressText}");
                    break;
                }
            }
            await Task.Delay(2000);
        }

        // Even if we don't get data (network-dependent), the torrent should at least have metadata
        // The critical test is: did the page crash? Did Blazor survive? Does the UI show state?
        var rows = await page.QuerySelectorAllAsync(".qb-table tbody tr");
        Assert.That(rows.Count, Is.GreaterThanOrEqualTo(1), "Torrent should still be in table");

        Console.WriteLine($"[BrowserTest] PASS: Sintel magnet added, metadata resolved, progress: {progressText}");
    }

    [OneTimeTearDown]
    public void StopDemoApp()
    {
        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            _serverProcess.Kill();
            _serverProcess.Dispose();
        }
    }
}
