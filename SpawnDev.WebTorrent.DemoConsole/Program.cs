using Microsoft.Extensions.DependencyInjection;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.DemoConsole;

var services = new ServiceCollection();
services.AddSingleton<WebTorrentClient>();
services.AddSingleton<DesktopTests>();
var sp = services.BuildServiceProvider();
var runner = new UnitTestRunner(sp, true);
await ConsoleRunner.Run(args, runner);
