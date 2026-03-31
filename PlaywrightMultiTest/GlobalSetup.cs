using System.Diagnostics;

namespace PlaywrightMultiTest
{
    [SetUpFixture]
    public class GlobalSetup
    {
        private static Process? _serverProcess;

        /// <summary>Desktop seeder for cross-platform P2P tests.</summary>
        public static DesktopSeeder? Seeder { get; set; }

        [OneTimeSetUp]
        public async Task SetUp()
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_ARGS", "--use-fake-ui-for-media-stream --use-fake-device-for-media-stream");

            // Start the WebTorrent ServerApp for integration tests
            await StartServerAppAsync();

            // Desktop seeder is started by ProjectRunner.Init() (after publish, before static server)
            // so the config file is written to wwwroot before the browser loads.
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            // Stop the desktop seeder
            if (Seeder != null)
            {
                await Seeder.DisposeAsync();
                Seeder = null;
            }

            // Stop the ServerApp
            if (_serverProcess != null)
            {
                if (!_serverProcess.HasExited)
                {
                    try { _serverProcess.Kill(entireProcessTree: true); }
                    catch { }
                }
                _serverProcess.Dispose();
                _serverProcess = null;
                Console.Error.WriteLine("[GlobalSetup] ServerApp stopped.");
            }
        }

        private static async Task StartServerAppAsync()
        {
            // Find the ServerApp project
            var current = Directory.GetCurrentDirectory();
            string? serverAppDir = null;

            // Walk up to find the solution root
            var dir = new DirectoryInfo(current);
            while (dir != null)
            {
                var serverProj = Path.Combine(dir.FullName, "SpawnDev.WebTorrent.ServerApp", "SpawnDev.WebTorrent.ServerApp.csproj");
                if (File.Exists(serverProj))
                {
                    serverAppDir = Path.GetDirectoryName(serverProj);
                    break;
                }
                dir = dir.Parent;
            }

            if (serverAppDir == null)
            {
                Console.Error.WriteLine("[GlobalSetup] ServerApp not found — integration tests will skip.");
                return;
            }

            Console.Error.WriteLine($"[GlobalSetup] Starting ServerApp from: {serverAppDir}");

            _serverProcess = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet", "run")
                {
                    WorkingDirectory = serverAppDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };

            _serverProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) Console.Error.WriteLine($"[ServerApp] {e.Data}");
            };
            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) Console.Error.WriteLine($"[ServerApp-err] {e.Data}");
            };

            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            // Wait for the server to be ready (check HTTP port)
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

            for (int i = 0; i < 30; i++) // up to 30 seconds
            {
                await Task.Delay(1000);
                try
                {
                    var response = await http.GetAsync("http://localhost:5561");
                    if (response.IsSuccessStatusCode)
                    {
                        Console.Error.WriteLine("[GlobalSetup] ServerApp is ready.");
                        return;
                    }
                }
                catch { }
            }

            Console.Error.WriteLine("[GlobalSetup] ServerApp failed to start within 30s — integration tests will skip.");
        }
    }
}
