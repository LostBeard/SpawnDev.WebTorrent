using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

namespace SpawnDev.WebTorrent.WpfDemo;

public partial class MainWindow : Window
{
    private readonly WebTorrentClient _client;
    private readonly ObservableCollection<TorrentViewModel> _torrents = new();
    private readonly DispatcherTimer _refreshTimer;
    private TorrentViewModel? _selectedVm;
    private string _currentTab = "general";

    private static readonly Dictionary<string, string> CCMagnets = new()
    {
        ["BigBuckBunny"] = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Big+Buck+Bunny&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Ftracker.files.fm%3A7073%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fbig-buck-bunny.torrent",
        ["Sintel"] = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Ftracker.files.fm%3A7073%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fsintel.torrent",
        ["CosmosLaundromat"] = "magnet:?xt=urn:btih:c9e15763f722f23e98a29decdfae341b98d53056&dn=Cosmos+Laundromat&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Ftracker.files.fm%3A7073%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fcosmos-laundromat.torrent",
        ["TearsOfSteel"] = "magnet:?xt=urn:btih:209c8226b299b308beaf2b9cd3fb49212dbd13ec&dn=Tears+of+Steel&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Ftracker.files.fm%3A7073%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Ftears-of-steel.torrent",
    };

    private TorrentHttpServer? _httpServer;

    public MainWindow()
    {
        InitializeComponent();
        _client = new WebTorrentClient();

        // Start HTTP server for media streaming
        try
        {
            _httpServer = _client.CreateServer(18770);
            Log($"HTTP server started: {_httpServer.BaseUrl}");
        }
        catch (Exception ex) { Log($"HTTP server failed: {ex.Message}"); }

        TorrentListView.ItemsSource = _torrents;
        StatusPeerId.Text = System.Text.Encoding.ASCII.GetString(_client.PeerId, 0, 8);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            foreach (var t in _torrents) t.Swarm.UpdateSpeed();
            RefreshUI();
        };
        _refreshTimer.Start();

        // Clean up resources on close
        Closing += (_, _) =>
        {
            _refreshTimer.Stop();
            _httpServer?.Stop();
            foreach (var t in _torrents) _ = t.Swarm.DisposeAsync();
            _ = _client.DisposeAsync();
        };

        Log("SpawnDev.WebTorrent Desktop Client initialized");

        // Handle command-line .torrent files (open with this app)
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            foreach (var arg in args.Skip(1))
            {
                if (arg.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(arg))
                {
                    try
                    {
                        var bytes = await System.IO.File.ReadAllBytesAsync(arg);
                        var metadata = Torrent.TorrentParser.Parse(bytes);
                        await AddFromMetadata(metadata);
                        Log($"Opened: {arg}");
                    }
                    catch (Exception ex) { Log($"Failed to open {arg}: {ex.Message}"); }
                }
                else if (arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    await AddMagnetAsync(arg, null);
                }
            }
        };
    }

    private async Task AddFromMetadata(Torrent.TorrentMetadata metadata)
    {
        var hash = Convert.ToHexString(metadata.InfoHash).ToLowerInvariant();
        if (_torrents.Any(t => t.HashFull == hash)) return;

        var swarm = await _client.AddAsync(metadata);
        var vm = new TorrentViewModel
        {
            Swarm = swarm,
            Name = metadata.Name,
            HashFull = hash,
            HashShort = hash[..8] + "...",
            SizeText = FormatBytes(metadata.TotalLength),
        };
        foreach (var f in metadata.Files)
            vm.Files.Add(new FileViewModel { Path = f.Path, SizeText = FormatBytes(f.Length), Ext = System.IO.Path.GetExtension(f.Path) });

        _torrents.Add(vm);
        TorrentListView.SelectedItem = vm;

        foreach (var ws in metadata.UrlList) swarm.AddWebSeed(ws.TrimEnd('/'));
        swarm.StartDownload();

        await FetchMetadataAndDownloadAsync(vm, "");
        await ConnectTrackersAsync(vm, "");
    }

    // ── Event Handlers ──

    private void MagnetInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(MagnetInput.Text))
            _ = AddMagnetAsync(MagnetInput.Text.Trim(), null);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(MagnetInput.Text))
            _ = AddMagnetAsync(MagnetInput.Text.Trim(), null);
    }

    private void QuickAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && CCMagnets.TryGetValue(tag, out var magnet))
            _ = AddMagnetAsync(magnet, btn.Content?.ToString());
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_client) { Owner = this };
        dlg.ShowDialog();
    }

    private void TestsButton_Click(object sender, RoutedEventArgs e)
    {
        var testWindow = new SpawnDev.UnitTesting.Desktop.UnitTestsWindow
        {
            TestTypes = new[] { typeof(DesktopTests) },
            AutoRun = true,
            Owner = this,
        };
        testWindow.Show();
    }

    private void TorrentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedVm = TorrentListView.SelectedItem as TorrentViewModel;
        UpdateDetailPanel();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tab)
        {
            _currentTab = tab;
            UpdateTabVisuals();
            UpdateDetailPanel();
        }
    }

    // ── Core Logic ──

    private async Task AddMagnetAsync(string magnetUri, string? displayName)
    {
        MagnetInput.Text = "";
        try
        {
            var swarm = await _client.AddAsync(magnetUri);
            var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
            if (_torrents.Any(t => t.HashFull == hash)) { Log($"Already added: {displayName ?? hash[..8]}"); return; }

            var vm = new TorrentViewModel
            {
                Swarm = swarm,
                Name = displayName ?? "Loading metadata...",
                HashFull = hash,
                HashShort = hash[..8] + "...",
            };
            _torrents.Add(vm);
            TorrentListView.SelectedItem = vm;
            Log($"Added: {vm.Name}");

            await FetchMetadataAndDownloadAsync(vm, magnetUri);
            await ConnectTrackersAsync(vm, magnetUri);
        }
        catch (Exception ex) { Log($"Error: {ex.Message}"); }
    }

    private async Task FetchMetadataAndDownloadAsync(TorrentViewModel vm, string magnetUri)
    {
        string? torrentUrl = null;
        var webSeedUrls = new List<string>();
        foreach (var part in magnetUri.Split('&'))
        {
            var p = part.Contains('?') ? part.Split('?').Last() : part;
            var eq = p.IndexOf('=');
            if (eq < 0) continue;
            var k = p[..eq];
            var v = Uri.UnescapeDataString(p[(eq + 1)..].Replace('+', ' '));
            if (k == "xs") torrentUrl = v;
            if (k == "ws") webSeedUrls.Add(v);
        }

        if (torrentUrl == null) { Log($"[{vm.Name}] No xs= URL"); return; }

        try
        {
            Log($"[{vm.Name}] Fetching .torrent...");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var torrentBytes = await http.GetByteArrayAsync(torrentUrl);
            var metadata = TorrentParser.Parse(torrentBytes);
            if (!metadata.InfoHash.SequenceEqual(vm.Swarm.InfoHash)) return;

            foreach (var ws in metadata.UrlList)
                if (!webSeedUrls.Contains(ws)) webSeedUrls.Add(ws);

            vm.Swarm.SetMetadata(metadata);
            vm.Name = metadata.Name;
            vm.SizeText = FormatBytes(metadata.TotalLength);

            vm.Files.Clear();
            foreach (var f in metadata.Files)
                vm.Files.Add(new FileViewModel { Path = f.Path, SizeText = FormatBytes(f.Length), Ext = System.IO.Path.GetExtension(f.Path) });

            vm.TrackerEntries.Clear();

            foreach (var ws in webSeedUrls) vm.Swarm.AddWebSeed(ws);
            vm.Swarm.StartDownload();

            vm.Swarm.OnDone += () => Dispatcher.Invoke(() =>
            {
                Log($"[{vm.Name}] Download complete!");
                Title = $"SpawnDev.WebTorrent — {vm.Name} complete!";
                // Flash taskbar
                System.Media.SystemSounds.Asterisk.Play();
                // Reset title after 5 seconds
                _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.Invoke(() =>
                    Title = "SpawnDev.WebTorrent — Desktop Client"));
            });

            Log($"[{vm.Name}] {FormatBytes(metadata.TotalLength)}, {metadata.PieceCount} pieces, {webSeedUrls.Count} web seed(s)");
        }
        catch (Exception ex) { Log($"[{vm.Name}] {ex.Message}"); }
    }

    private async Task ConnectTrackersAsync(TorrentViewModel vm, string magnetUri)
    {
        var trackers = new List<string>();
        foreach (var part in magnetUri.Split('&'))
        {
            var p = part.Contains('?') ? part.Split('?').Last() : part;
            if (p.StartsWith("tr="))
            {
                var url = Uri.UnescapeDataString(p[3..].Replace('+', ' '));
                if (url.StartsWith("wss://")) trackers.Add(url);
            }
        }
        if (trackers.Count == 0) trackers.AddRange(new[] { "wss://hub.spawndev.com:44365/announce", "wss://tracker.openwebtorrent.com" });

        // Create platform WebRTC transport for P2P
        var webRtc = IWebRtcTransport.Create();

        // PeerCoordinator handles WebRTC signaling via trackers
        var coordinator = new PeerCoordinator(_client, vm.Swarm.InfoHash, webRtc);
        vm.Coordinator = coordinator;

        coordinator.OnPeerConnected += (peer) => Dispatcher.Invoke(() =>
        {
            Log($"[{vm.Name}] P2P connected: {peer.PeerId[..Math.Min(12, peer.PeerId.Length)]}");
            _ = vm.Swarm.AddConnectedPeerAsync(peer.Wire, new PeerInfo { Address = peer.PeerId, Source = "webrtc" });
        });
        coordinator.OnPeerDisconnected += (peer) => Dispatcher.Invoke(() =>
        {
            Log($"[{vm.Name}] P2P disconnected: {peer.PeerId[..Math.Min(12, peer.PeerId.Length)]}");
        });

        foreach (var url in trackers)
        {
            _ = ConnectSingleTrackerAsync(vm, coordinator, url);
        }
    }

    private async Task ConnectSingleTrackerAsync(TorrentViewModel vm, PeerCoordinator coordinator, string url)
    {
        var te = new TrackerViewModel { Url = url, Status = "Connecting..." };
        Dispatcher.Invoke(() => vm.TrackerEntries.Add(te));
        try
        {
            await coordinator.AddTrackerAsync(url);
            te.Status = "Connected";
            Dispatcher.Invoke(() => Log($"[{vm.Name}] Tracker + signaling: {new Uri(url).Host}"));
        }
        catch (Exception ex)
        {
            te.Status = "Failed";
            Dispatcher.Invoke(() => Log($"[{vm.Name}] Tracker {new Uri(url).Host} failed: {ex.Message}"));
        }
    }

    // ── UI Updates ──

    private void RefreshUI()
    {
        var totalDown = _torrents.Sum(t => t.Swarm.DownloadSpeed);
        var totalUp = _torrents.Sum(t => t.Swarm.UploadSpeed);
        StatusTorrents.Text = $"{_torrents.Count} torrents";
        StatusPeers.Text = $"{_torrents.Sum(t => t.Swarm.PeerCount)} peers";
        StatusDL.Text = FormatSpeed(totalDown);
        StatusUL.Text = FormatSpeed(totalUp);
        DownSpeedText.Text = FormatSpeed(totalDown);
        UpSpeedText.Text = FormatSpeed(totalUp);

        foreach (var vm in _torrents)
        {
            var pm = vm.Swarm.PieceManager;
            vm.ProgressPercent = (pm?.Progress ?? 0) * 100;
            vm.ProgressText = $"{vm.ProgressPercent:F1}%";
            vm.PeerCount = vm.Swarm.PeerCount;
            vm.DownSpeedText = vm.Swarm.DownloadSpeed > 0 ? FormatSpeed(vm.Swarm.DownloadSpeed) : "";
            vm.UpSpeedText = vm.Swarm.UploadSpeed > 0 ? FormatSpeed(vm.Swarm.UploadSpeed) : "";
            vm.StatusText = pm?.IsComplete == true ? "Seeding" : pm != null && pm.CompletedCount > 0 ? "Downloading" : vm.Swarm.HasMetadata ? "Waiting" : "Metadata";
            var eta = vm.Swarm.TimeRemaining;
            vm.EtaText = eta < 0 ? (pm?.IsComplete == true ? "" : "∞") : eta < 60000 ? $"{eta / 1000}s" : eta < 3600000 ? $"{eta / 60000}m" : $"{eta / 3600000}h";
            vm.Notify();
        }

        if (_selectedVm != null && _currentTab == "general")
            UpdatePieceMap();
    }

    private void UpdateDetailPanel()
    {
        if (_selectedVm == null) return;
        var vm = _selectedVm;
        var pm = vm.Swarm.PieceManager;

        DetailName.Text = vm.Swarm.Metadata?.Name ?? vm.Name ?? "—";
        DetailSize.Text = vm.Swarm.HasMetadata ? FormatBytes(vm.Swarm.Metadata!.TotalLength) : "—";
        DetailPieces.Text = pm != null ? $"{pm.CompletedCount} / {pm.PieceCount}" : "—";
        DetailDownloaded.Text = FormatBytes(vm.Swarm.Downloaded);
        DetailUploaded.Text = FormatBytes(vm.Swarm.Uploaded);
        DetailRatio.Text = vm.Swarm.Ratio.ToString("F3");
        DetailEta.Text = vm.EtaText;
        DetailPeerCount.Text = $"{vm.Swarm.PeerCount + (vm.Coordinator?.PeerCount ?? 0)}";
        DetailHash.Text = vm.HashFull;

        PanelFiles.ItemsSource = vm.Files;
        PanelTrackers.ItemsSource = vm.TrackerEntries;

        UpdateTabVisuals();
        UpdatePieceMap();
    }

    private void UpdateTabVisuals()
    {
        // Tab highlighting
        TabGeneral.Foreground = _currentTab == "general" ? (Brush)FindResource("AccentGreen") : (Brush)FindResource("TextMuted");
        TabGeneral.BorderBrush = _currentTab == "general" ? (Brush)FindResource("AccentGreen") : Brushes.Transparent;
        foreach (var (btn, tab) in new[] { (TabFiles, "files"), (TabPeers, "peers"), (TabTrackers, "trackers"), (TabLog, "log") })
        {
            btn.Foreground = _currentTab == tab ? (Brush)FindResource("AccentGreen") : (Brush)FindResource("TextMuted");
            btn.BorderBrush = _currentTab == tab ? (Brush)FindResource("AccentGreen") : Brushes.Transparent;
        }
        TabGeneral.Foreground = _currentTab == "general" ? (Brush)FindResource("AccentGreen") : (Brush)FindResource("TextMuted");
        TabGeneral.BorderBrush = _currentTab == "general" ? (Brush)FindResource("AccentGreen") : Brushes.Transparent;

        PanelGeneral.Visibility = _currentTab == "general" ? Visibility.Visible : Visibility.Collapsed;
        PanelFiles.Visibility = _currentTab == "files" ? Visibility.Visible : Visibility.Collapsed;
        PanelPeers.Visibility = _currentTab == "peers" ? Visibility.Visible : Visibility.Collapsed;
        PanelTrackers.Visibility = _currentTab == "trackers" ? Visibility.Visible : Visibility.Collapsed;
        PanelLog.Visibility = _currentTab == "log" ? Visibility.Visible : Visibility.Collapsed;

        if (_currentTab == "peers" && _selectedVm != null)
        {
            var peerCount = _selectedVm.Swarm.PeerCount + (_selectedVm.Coordinator?.PeerCount ?? 0);
            PanelPeers.Text = $"{peerCount} connected peer(s)\nWeb seeds: {_selectedVm.Swarm.WebSeedCount}\n\nPeers connect via WebSocket tracker signaling and WebRTC data channels.\nDesktop peers use SIPSorcery, browser peers use SpawnDev.BlazorJS.";
        }
    }

    private void UpdatePieceMap()
    {
        PieceMapPanel.Children.Clear();
        var bf = _selectedVm?.Swarm.PieceManager?.Bitfield;
        if (bf == null) return;

        int total = bf.Length;
        int cols = Math.Min(total, 120);
        int step = Math.Max(1, total / cols);

        for (int i = 0; i < cols; i++)
        {
            int start = i * step;
            int end = Math.Min((i + 1) * step, total);
            bool any = false;
            for (int j = start; j < end; j++) if (bf[j]) { any = true; break; }

            var rect = new Rectangle
            {
                Width = 5, Height = 5,
                Fill = any ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) : new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0.5),
                RadiusX = 1, RadiusY = 1,
            };
            PieceMapPanel.Children.Add(rect);
        }
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        LogText.Text += line;
    }

    private async void SeedTest_Click(object sender, RoutedEventArgs e)
    {
        var data = new byte[65536];
        Random.Shared.NextBytes(data);
        var name = $"SpawnDev-Test-{DateTime.Now:HHmmss}.bin";

        var swarm = await _client.SeedAsync(data, name,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { "wss://hub.spawndev.com:44365/announce", "wss://tracker.openwebtorrent.com" },
                Comment = "Test torrent from SpawnDev.WebTorrent WPF demo",
            });

        var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
        var vm = new TorrentViewModel
        {
            Swarm = swarm, Name = name, HashFull = hash, HashShort = hash[..8] + "...",
            SizeText = FormatBytes(data.Length),
        };
        _torrents.Add(vm);
        TorrentListView.SelectedItem = vm;

        await ConnectTrackersAsync(vm, swarm.MagnetURI);
        Log($"Seeding: {name}, magnet: {swarm.MagnetURI[..Math.Min(60, swarm.MagnetURI.Length)]}...");
    }

    // ── Pause/Resume/Filter ──
    private void PauseAll_Click(object sender, RoutedEventArgs e) { foreach (var t in _torrents) t.Swarm.Pause(); }
    private void ResumeAll_Click(object sender, RoutedEventArgs e) { foreach (var t in _torrents) t.Swarm.Resume(); }
    private void FilterAll_Click(object sender, RoutedEventArgs e) { TorrentListView.ItemsSource = _torrents; }
    private void FilterDownloading_Click(object sender, RoutedEventArgs e) { TorrentListView.ItemsSource = _torrents.Where(t => !t.Swarm.Done && t.Swarm.HasMetadata).ToList(); }
    private void FilterSeeding_Click(object sender, RoutedEventArgs e) { TorrentListView.ItemsSource = _torrents.Where(t => t.Swarm.Done).ToList(); }
    private void FilterPaused_Click(object sender, RoutedEventArgs e) { TorrentListView.ItemsSource = _torrents.Where(t => t.Swarm.Paused).ToList(); }

    // ── Drag & Drop ──

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            e.Effects = files.Any(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                ? DragDropEffects.Copy : DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;

        foreach (var file in files.Where(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var torrentBytes = await System.IO.File.ReadAllBytesAsync(file);
                var metadata = Torrent.TorrentParser.Parse(torrentBytes);
                var hash = Convert.ToHexString(metadata.InfoHash).ToLowerInvariant();

                if (_torrents.Any(t => t.HashFull == hash))
                {
                    Log($"Already added: {metadata.Name}");
                    continue;
                }

                var swarm = await _client.AddAsync(metadata);
                var vm = new TorrentViewModel
                {
                    Swarm = swarm,
                    Name = metadata.Name,
                    HashFull = hash,
                    HashShort = hash[..8] + "...",
                    SizeText = FormatBytes(metadata.TotalLength),
                };
                foreach (var f in metadata.Files)
                    vm.Files.Add(new FileViewModel { Path = f.Path, SizeText = FormatBytes(f.Length), Ext = System.IO.Path.GetExtension(f.Path) });

                _torrents.Add(vm);
                TorrentListView.SelectedItem = vm;

                // Add web seeds and start
                foreach (var ws in metadata.UrlList) swarm.AddWebSeed(ws.TrimEnd('/'));
                swarm.StartDownload();

                Log($"Added via drag-drop: {metadata.Name}");
            }
            catch (Exception ex) { Log($"Drop error: {ex.Message}"); }
        }
    }

    private void CopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm == null) return;
        var magnet = _selectedVm.Swarm.MagnetURI;
        Clipboard.SetText(magnet);
        Log("Magnet URI copied to clipboard");
    }

    private void ExportTorrent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm?.Swarm.TorrentFileBytes == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_selectedVm.Name}.torrent",
            Filter = "Torrent files (*.torrent)|*.torrent",
        };
        if (dlg.ShowDialog() == true)
        {
            System.IO.File.WriteAllBytes(dlg.FileName, _selectedVm.Swarm.TorrentFileBytes);
            Log($"Exported: {dlg.FileName}");
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;

        var data = await System.IO.File.ReadAllBytesAsync(dlg.FileName);
        var name = System.IO.Path.GetFileName(dlg.FileName);
        Log($"Loading: {name} ({FormatBytes(data.Length)})...");

        var swarm = await _client.SeedAsync(data, name,
            new TorrentCreatorOptions
            {
                PieceLength = data.Length > 1048576 ? 262144 : 16384,
                Trackers = new[] { "wss://hub.spawndev.com:44365/announce", "wss://tracker.openwebtorrent.com" },
            });

        var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
        var vm = new TorrentViewModel
        {
            Swarm = swarm, Name = name, HashFull = hash, HashShort = hash[..8] + "...",
            SizeText = FormatBytes(data.Length),
        };
        _torrents.Add(vm);
        TorrentListView.SelectedItem = vm;
        await ConnectTrackersAsync(vm, swarm.MagnetURI);
        Log($"Seeding: {name}, {swarm.PieceManager!.PieceCount} pieces");
    }

    private void PanelFiles_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_selectedVm != null && PanelFiles.SelectedItem is FileViewModel file)
            PlayFile(_selectedVm, file);
    }

    public void PlayFile(TorrentViewModel vm, FileViewModel file)
    {
        if (_httpServer == null) { Log("HTTP server not running"); return; }
        var ext = file.Ext.ToLowerInvariant();
        if (ext is not ".mp4" and not ".webm" and not ".avi" and not ".mkv" and not ".mp3" and not ".wav" and not ".ogg")
        {
            Log($"Unsupported media format: {ext}");
            return;
        }

        var hash = vm.HashFull;
        var url = $"{_httpServer.BaseUrl}{hash}/{Uri.EscapeDataString(file.Path)}";
        Log($"Playing: {url}");

        var player = new MediaPlayerWindow(url, file.Path) { Owner = this };
        player.Show();
    }

    private static string FormatBytes(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b / 1024.0:F1} KB" : b < 1073741824 ? $"{b / 1048576.0:F1} MB" : $"{b / 1073741824.0:F2} GB";
    private static string FormatSpeed(double bps) => bps < 1024 ? $"{bps:F0} B/s" : bps < 1048576 ? $"{bps / 1024.0:F1} KB/s" : $"{bps / 1048576.0:F1} MB/s";
}

// ── View Models ──

public class TorrentViewModel : INotifyPropertyChanged
{
    public TorrentSwarm Swarm { get; init; } = null!;
    public string Name { get; set; } = "";
    public string HashFull { get; set; } = "";
    public string HashShort { get; set; } = "";
    public string SizeText { get; set; } = "—";
    public string ProgressText { get; set; } = "0.0%";
    public double ProgressPercent { get; set; }
    public int PeerCount { get; set; }
    public string StatusText { get; set; } = "Metadata";
    public string DownSpeedText { get; set; } = "";
    public string UpSpeedText { get; set; } = "";
    public string EtaText { get; set; } = "";
    public ObservableCollection<FileViewModel> Files { get; } = new();
    public ObservableCollection<TrackerViewModel> TrackerEntries { get; } = new();
    public PeerCoordinator? Coordinator { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressPercent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeerCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
    }
}

public class FileViewModel
{
    public string Path { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string Ext { get; set; } = "";
    public string ProgressText { get; set; } = "0%";
}

public class TrackerViewModel : INotifyPropertyChanged
{
    private string _status = "";
    public string Url { get; set; } = "";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
