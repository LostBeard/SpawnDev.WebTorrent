using Microsoft.Playwright;
using SpawnDev.UnitTesting;
using System.Diagnostics;
using System.Text.Json;

namespace PlaywrightMultiTest
{
    public class ProjectRunner
    {
        public static ProjectRunner Instance => GetRunner().GetAwaiter().GetResult()!;
        private static Task<ProjectRunner>? _projectRunner;
        public List<TestableProject> TestableProjects { get; } = new List<TestableProject>();

        /// <summary>
        /// Returns an initialized ProjectRunner singleton
        /// </summary>
        /// <returns></returns>
        static Task<ProjectRunner> GetRunner() => _projectRunner ??= new Func<Task<ProjectRunner>>(async () =>
        {
            var ret = new ProjectRunner();
            await ret.Init().ConfigureAwait(false);
            return ret;
        })();

        /// <summary>
        /// Private consturoctor to prevent external instantiation. The runner should only be created through the GetRunner property which ensures proper initialization.
        /// </summary>
        private ProjectRunner() { }

        private static async Task<int> RunDotnetAsync(string args, string workingDir, int timeoutMs = 300000)
        {
            LogStatus($"RunDotnetAsync: dotnet {args.Split(' ')[0]} (timeout={timeoutMs/1000}s)");
            var startInfo = new ProcessStartInfo("dotnet", args)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = new Process();
            p.StartInfo = startInfo;
            p.EnableRaisingEvents = true;

            // Use event-based async reads to avoid pipe buffer deadlocks
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };

            var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            p.Exited += (_, _) => exitTcs.TrySetResult(true);

            p.Start();
            LogStatus($"RunDotnetAsync: started PID={p.Id}");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Wait for exit or timeout
            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(() => exitTcs.TrySetResult(false));
            var exited = await exitTcs.Task.ConfigureAwait(false);

            if (exited)
            {
                // WaitForExit() with no args can hang if child processes still hold
                // redirected stream handles. Use a short timed wait instead.
                p.WaitForExit(5000);
                LogStatus($"RunDotnetAsync: done PID={p.Id} exit={p.ExitCode}");
                return p.ExitCode;
            }
            else
            {
                LogStatus($"RunDotnetAsync: TIMEOUT after {timeoutMs / 1000}s, killing PID={p.Id}...");
                try { p.Kill(entireProcessTree: true); } catch { }
                return -1;
            }
        }
        /// <summary>
        /// Async initialization method for the ProjectRunner. This is where you can perform any setup that needs to happen before tests are enumerated, such as reading configuration files, setting up logging, etc.
        /// </summary>
        /// <returns></returns>
        // Status file for diagnosing startup hangs
        private static readonly string StatusFile = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "init_status.log");
        private static void LogStatus(string msg)
        {
            var tid = Environment.CurrentManagedThreadId;
            var isPool = Thread.CurrentThread.IsThreadPoolThread;
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [T{tid}{(isPool ? ",pool" : "")}] {msg}";
            Console.Error.WriteLine($"[PlaywrightMultiTest] {msg}");
            try { File.AppendAllText(StatusFile, line + "\n"); } catch { }
        }

        private async Task Init()
        {
            try { File.WriteAllText(StatusFile, ""); } catch { } // clear
            LogStatus("Init() started");

            string[] args = Environment.GetCommandLineArgs();
            // Support both --filter=VALUE and --filter VALUE formats
            var filter = args.LastOrDefault(o => o.StartsWith("--filter="))?.Substring(9);
            if (filter == null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--filter")
                    {
                        filter = args[i + 1];
                        break;
                    }
                }
            }

            // The --filter parsing above stays FIRST - some PMT setups do get the arg through, and where it
            // arrives it wins. But when the run goes through `dotnet test`, the NUnit adapter consumes
            // --filter and it never reaches this testhost, so a "scoped" run silently enumerates and runs
            // EVERYTHING (measured here 2026-08-29: `PMT_FILTER=DeselectedRead` ran 1029 tests, and reading
            // that green sweep as evidence about one test was simply wrong). PMT_FILTER is an environment
            // variable, which this process CAN always read, and is the same mechanism ILGPU.ML's PMT uses:
            //     PMT_FILTER=LazyHash dotnet test PlaywrightMultiTest/PlaywrightMultiTest.csproj
            // Matching is SUBSTRING (also matching ILGPU.ML), so a filter can select a whole family by
            // prefix rather than naming one method exactly.
            filter ??= Environment.GetEnvironmentVariable("PMT_FILTER");
            if (!string.IsNullOrEmpty(filter)) LogStatus($"Test filter active: '{filter}' (substring match)");

            LogStatus("Discovering projects...");
            var projects = ProjectDiscovery.GetWorkspaceRoot();
            LogStatus($"Found {projects.Count()} projects");
            // add tests to _tests list based on the projects found. You can use the ProjectDetails to determine what kind of project it is and how to get the tests from it. For example, if it's a Blazor WASM project, you might want to start a Playwright instance and navigate to the app to get the tests. If it's a console app, you might want to run the exe with a specific argument to get the tests.
            foreach (var project in projects)
            {
                if (project.AppProjectType == ProjectType.BlazorWasm)
                {
                    var testableProject = new TestableBlazorWasm
                    {
                        ProjectDetails = project,
                    };
                    TestableProjects.Add(testableProject);

                    var buildTest = new ProjectTest(testableProject, $"Build {project.Name}");
                    testableProject.Tests.Add(buildTest);

                    var indexPath = Path.Combine(testableProject.ProjectDetails.WwwRoot, "index.html");

                    // build a publish version of the app for testing
                    LogStatus($"Publishing {project.Name}...");
                    var pubResult = await RunDotnetAsync($"publish \"{project.CsprojPath}\" -c Release", project.Directory).ConfigureAwait(false);
                    LogStatus($"Publish {project.Name}: exit={pubResult}");
                    if (pubResult != 0 || !File.Exists(indexPath))
                    {
                        // build failed
                        buildTest.SetError();
                        continue;
                    }

                    try
                    {
                        LogStatus("Installing Playwright browsers...");
                        var exitCode = Microsoft.Playwright.Program.Main(new[] { "install" });

                        if (exitCode != 0)
                        {
                            throw new Exception($"Playwright browser installation failed with exit code {exitCode}");
                        }

                        // start a static file server to serve the published output
                        // Fixed port so IndexedDB persists across runs (same origin = same IDB)
                        var _port = 5562;
                        var baseUrl = $"https://localhost:{_port}/";
                        // Start desktop seeder and write config to wwwroot before static server starts
                        if (GlobalSetup.Seeder == null)
                        {
                            try
                            {
                                GlobalSetup.Seeder = new DesktopSeeder();
                                await GlobalSetup.Seeder.StartAsync();
                                LogStatus($"Desktop seeder started: {GlobalSetup.Seeder.MagnetUri?[..Math.Min(80, GlobalSetup.Seeder.MagnetUri?.Length ?? 0)]}");
                            }
                            catch (Exception ex)
                            {
                                LogStatus($"Desktop seeder failed: {ex.Message}");
                                GlobalSetup.Seeder = null;
                            }
                        }
                        if (GlobalSetup.Seeder?.IsSeeding == true)
                            GlobalSetup.Seeder.WriteTestConfig(testableProject.ProjectDetails.WwwRoot);

                        testableProject.Server = new StaticFileServer(testableProject.ProjectDetails.WwwRoot, baseUrl);
                        // start https server to serve the Blazor WASM app
                        testableProject.Server.Start();

                        // create a playwright browser, navigate to the app, and enumerate the tests
                        LogStatus("Creating Playwright instance...");
                        testableProject.Playwright = await Playwright.CreateAsync().ConfigureAwait(false);
                        // launch browser
                        // Use persistent context so IndexedDB, localStorage, and
                        // File System Access permissions survive across test runs.
                        // This enables ShaderDebugService's debug folder persistence.
                        var userDataDir = Path.Combine(Path.GetTempPath(), "SpawnDev.WebTorrent.PlaywrightProfile");
                        Directory.CreateDirectory(userDataDir);
                        LogStatus($"Launching Chromium (persistent profile: {userDataDir})...");
                        testableProject.BrowserContext = await testableProject.Playwright.Chromium.LaunchPersistentContextAsync(
                            userDataDir,
                            new BrowserTypeLaunchPersistentContextOptions
                            {
                                Headless = false,
                                // Use the installed Google Chrome (NOT Playwright's bundled open-source Chromium,
                                // which ships WITHOUT the proprietary H.264/AAC decoders). Required so <video>
                                // media-streaming tests can actually demux real-world H.264 mp4s (e.g. Sintel);
                                // the bundled Chromium silently stalls at readyState=0 on H.264 with no error.
                                Channel = "chrome",
                                Args = new[]
                                {
                                    "--enable-unsafe-webgpu",
                                    "--enable-features=Vulkan,WebGPUService,SkiaGraphite,FileSystemAccessPersistentPermission",
                                    "--ignore-gpu-blocklist",
                                    "--no-sandbox",
                                    // Auto-grant file system write permission (no prompt)
                                    "--disable-features=FileSystemAccessPermissionPrompt",
                                    "--allow-file-access-from-files"
                                }
                            }).ConfigureAwait(false);
                        testableProject.Browser = testableProject.BrowserContext.Browser;
                        // Grant all available permissions to avoid prompts
                        await testableProject.BrowserContext.GrantPermissionsAsync(
                            new[] { "clipboard-read", "clipboard-write" }).ConfigureAwait(false);
                        // new page
                        testableProject.Page = await testableProject.BrowserContext.NewPageAsync().ConfigureAwait(false);

                        // Temporary: capture browser console output containing WGSL dumps to a log file
                        var wgslDumpDir = Path.Combine(project.Directory, "..", "PlaywrightMultiTest", "WGSLDumps");
                        Directory.CreateDirectory(wgslDumpDir);
                        var consoleLogPath = Path.Combine(wgslDumpDir, "browser_console.log");
                        File.WriteAllText(consoleLogPath, ""); // clear previous log
                        var wasmDumpChunks = new System.Collections.Generic.List<string>();
                        testableProject.Page.Console += (_, msg) =>
                        {
                            var text = msg.Text;
                            // Capture Wasm binary dumps: collect base64 chunks and write to disk
                            if (text.StartsWith("[Wasm_DUMP]"))
                            {
                                wasmDumpChunks.Add(text.Substring("[Wasm_DUMP]".Length));
                            }
                            else if (text.StartsWith("[Wasm_DUMP_END]") && wasmDumpChunks.Count > 0)
                            {
                                try
                                {
                                    var b64 = string.Join("", wasmDumpChunks);
                                    var bytes = Convert.FromBase64String(b64);
                                    var wasmPath = Path.Combine(wgslDumpDir, $"wasm_dump_{DateTime.Now:HHmmss}.wasm");
                                    File.WriteAllBytes(wasmPath, bytes);
                                    LogStatus($"Wasm binary dumped: {wasmPath} ({bytes.Length} bytes)");
                                }
                                catch (Exception ex) { LogStatus($"Wasm dump failed: {ex.Message}"); }
                                wasmDumpChunks.Clear();
                            }
                            else if (text.StartsWith("[Wasm_DUMP_START]"))
                            {
                                wasmDumpChunks.Clear();
                            }
                            // Only log messages related to WGSL dumps, Wasm worker traces, or errors
                            if (text.Contains("WGSL") || text.Contains("@compute") || text.Contains("@workgroup_size") || text.Contains("WGSL_DUMP") || text.Contains("GLSL_DUMP") || text.Contains("[WasmWorker]") || text.Contains("[Wasm") || text.Contains("CONV2D_TRACE") || text.Contains("TEX_UNIT") || text.Contains("PREPROCESS_TRACE") || text.Contains("LAYER_TRACE") || text.Contains("LOGITS_TRACE") || text.Contains("CPU_LOGITS") || text.Contains("DISP_TRACE") || text.Contains("TF_OFFSET") || msg.Type == "error")
                            {
                                try
                                {
                                    File.AppendAllText(consoleLogPath, $"[{msg.Type}] {text}\n---END_MSG---\n");
                                }
                                catch { }
                            }
                        };

                        // go to the app's unit tests page.
                        var testPageUrl = new Uri(new Uri(baseUrl), testableProject.TestPage).ToString();
                        LogStatus($"Navigating to {testPageUrl}...");
                        await testableProject.Page.GotoAsync(testPageUrl).ConfigureAwait(false);
                        LogStatus("Page loaded, waiting for test table...");

                        // wait for tests to load
                        await testableProject.Page.WaitForSelectorAsync("table.unit-test-ready", new() { Timeout = 30000 }).ConfigureAwait(false);
                        LogStatus("Test table ready");

                        // Enumerate test rows via a single Page.EvaluateAsync round-trip
                        // instead of one IPC per cell. With ~1000+ rows the per-row
                        // approach took 2-5 minutes of dead time after page render;
                        // batch scrape collapses that to sub-second. Per Tuvok's
                        // 2026-04-25 PMT-enumeration-speedup DevComms.
                        var rowsJson = await testableProject.Page.EvaluateAsync<JsonElement>(@"() => {
                            const rows = document.querySelectorAll('table.unit-test-view tbody tr');
                            return Array.from(rows).map(r => ({
                                typeName: r.querySelector('.test-type-name')?.textContent ?? '',
                                methodName: r.querySelector('.test-method-name')?.textContent ?? ''
                            }));
                        }").ConfigureAwait(false);

                        int totalRows = rowsJson.GetArrayLength();
                        for (int i = 0; i < totalRows; i++)
                        {
                            var row = rowsJson[i];
                            var typeName = row.GetProperty("typeName").GetString() ?? "";
                            var methodName = row.GetProperty("methodName").GetString() ?? "";

                            var rowTest = new ProjectTest(testableProject, typeName, methodName, testPageUrl);

                            if (filter != null)
                            {
                                if (!MatchesFilter(rowTest, filter)) continue;
                            }

                            testableProject.Tests.Add(rowTest);
                        }
                        LogStatus($"Browser tests enumerated: {testableProject.Tests.Count} tests");

                    }
                    catch (Exception ex)
                    {
                        LogStatus($"Error initializing {project.Name}: {ex.Message}");
                    }
                }
                else if (project.AppProjectType == ProjectType.Exe)
                {
                    // enumerate tests by calling the console app. by default it will return a list of the tests in the exe

                    var testableProject = new TestableConsole
                    {
                        ProjectDetails = project,
                    };
                    TestableProjects.Add(testableProject);

                    var buildTest = new ProjectTest(testableProject, $"Build {project.Name}");
                    testableProject.Tests.Add(buildTest);

                    // build a publish version of the app for testing
                    LogStatus($"Publishing {project.Name}...");
                    var pubResult = await RunDotnetAsync($"publish \"{project.CsprojPath}\" -c Release", project.Directory).ConfigureAwait(false);
                    LogStatus($"Publish {project.Name}: exit={pubResult}");
                    var publishedBinary = project.ExistingPublishBinary;
                    if (pubResult != 0 || string.IsNullOrEmpty(publishedBinary))
                    {
                        // build failed
                        buildTest.SetError();
                        continue;
                    }

                    // get list of tests by running the exe with a specific argument
                    LogStatus($"Enumerating tests from {Path.GetFileName(publishedBinary)}...");
                    var result = await ProcessRunner.Run(publishedBinary).ConfigureAwait(false);
                    LogStatus($"Enumeration done: exit={result.ExitCode}, lines={result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}");
                    var testList = result.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    foreach (var test in testList)
                    {
                        // get test type name
                        var typeName = test.Split(".")[0];

                        // get test method name
                        var methodName = test.Split(".")[1];

                        var rowTest = new ProjectTest(testableProject, typeName!, methodName!);
                        if (filter != null)
                        {
                            if (!MatchesFilter(rowTest, filter)) continue;
                        }
                        testableProject.Tests.Add(rowTest);

                        rowTest.TestFunc = async (page) =>
                        {
                            var runArgs = rowTest.Name;
                            var result = await ProcessRunner.Run(publishedBinary, runArgs, timeout: 120_000).ConfigureAwait(false);
                            var resultLines = result.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            var testResltTest = resultLines.LastOrDefault(o => o.StartsWith("TEST: "))?.Substring(6);
                            var unitTest = testResltTest != null ? JsonSerializer.Deserialize<UnitTest>(testResltTest) : null;
                            if (unitTest == null)
                            {
                                throw new Exception("Test run failed");
                            }
                            var stateMessage = unitTest.ResultText;
                            rowTest.Result = unitTest.Result;

                            if (rowTest.Result == TestResult.Unsupported)
                            {
                                if (string.IsNullOrWhiteSpace(stateMessage))
                                {
                                    stateMessage = "Skipped";
                                }
                            }
                            else if (rowTest.Result == TestResult.Error)
                            {
                                if (string.IsNullOrWhiteSpace(stateMessage))
                                {
                                    stateMessage = "Failed";
                                }
                                rowTest.ResultMessage = stateMessage;
                                throw new Exception(stateMessage);
                            }
                            else
                            {
                                if (string.IsNullOrWhiteSpace(stateMessage))
                                {
                                    stateMessage = "Success";
                                }
                            }

                            rowTest.ResultMessage = stateMessage;
                            rowTest.Result = unitTest.Result;
                            var nmtt = true;
                        };
                    }

                    var nmt11 = true;
                }
            }
            LogStatus($"Init() complete. Total projects={TestableProjects.Count}, " +
                $"total tests={TestableProjects.Sum(p => p.Tests.Count)}");
            var nmt = true;
        }
        IEnumerable<TestCaseData>? _TestCases;
        public IEnumerable<TestCaseData> TestCases => _TestCases ??= GetPlaywrightTasks();

        /// <summary>
        /// Returns all the tests that are found. This is called before StartUp, so you should not rely on any services or infrastructure being available when this is called. You can return any tests you want to run here, and they will be run by the test runner.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<TestCaseData> GetPlaywrightTasks()
        {
            Debug.WriteLine("GetPlaywrightTasks()");
            foreach (var testableProject in TestableProjects)
            {
                foreach (var test in testableProject.Tests)
                {
                    var testCaseData = new TestCaseData(test).SetName(test.Name).SetCategory(test.TestTypeName ?? test.Name);
                    yield return testCaseData;
                }
            }
            var nmt = true;
        }

        /// <summary>
        /// This is called after tests have been enumerated bu before they are run. You can use this to start up any services or infrastructure needed for the tests.
        /// </summary>
        /// <returns></returns>
        public async Task StartUp()
        {
            Debug.WriteLine("StartUp()");
        }

        /// <summary>
        /// This is called after tests have ran. You can use this to stop any services or infrastructure started in StartUp.
        /// </summary>
        /// <returns></returns>
        public async Task Shutdown()
        {
            Debug.WriteLine("Shutdown()");
            foreach (var testableProject in TestableProjects)
            {
                if (testableProject is TestableBlazorWasm blazorProj)
                {
                    try { if (blazorProj.Page != null) await blazorProj.Page.CloseAsync().ConfigureAwait(false); } catch { }
                    try { if (blazorProj.BrowserContext != null) await blazorProj.BrowserContext.CloseAsync().ConfigureAwait(false); } catch { }
                    try { if (blazorProj.Browser != null) await blazorProj.Browser.CloseAsync().ConfigureAwait(false); } catch { }
                    try { blazorProj.Playwright?.Dispose(); } catch { }
                    try { if (blazorProj.Server != null) await blazorProj.Server.Stop().ConfigureAwait(false); } catch { }
                }
                else if (testableProject is TestableConsole consoleProj)
                {
                    // do any cleanup needed for console projects
                }
            }
        }
    
    // Substring match on the full name, the type, or the method. Kept in one place so the two enumeration
    // paths (browser rows and console rows) can never drift apart on what a filter means.
    private static bool MatchesFilter(ProjectTest test, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return (test.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (test.TestTypeName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (test.TestMethodName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
}