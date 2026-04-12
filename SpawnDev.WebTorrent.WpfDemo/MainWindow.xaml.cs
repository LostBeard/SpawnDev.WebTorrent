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
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Threading;

namespace SpawnDev.WebTorrent.WpfDemo;

public partial class MainWindow : Window
{
    private readonly WebTorrentClient _client;
    private readonly ObservableCollection<TorrentViewModel> _torrents = new();
    private readonly DispatcherTimer _refreshTimer;
    private TorrentViewModel? _selectedVm;
    private string _currentTab = "general";

    // Official WebTorrent free torrents (https://github.com/webtorrent/webtorrent/blob/master/docs/free-torrents.md)
    // Plus hub.spawndev.com tracker for our ecosystem
    private static readonly Dictionary<string, string> CCMagnets = new()
    {
        ["BigBuckBunny"] = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Big+Buck+Bunny&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.fastcast.nz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fbig-buck-bunny.torrent",
        ["Sintel"] = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.fastcast.nz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fsintel.torrent",
        ["CosmosLaundromat"] = "magnet:?xt=urn:btih:c9e15763f722f23e98a29decdfae341b98d53056&dn=Cosmos+Laundromat&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.fastcast.nz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fcosmos-laundromat.torrent",
        ["TearsOfSteel"] = "magnet:?xt=urn:btih:209c8226b299b308beaf2b9cd3fb49212dbd13ec&dn=Tears+of+Steel&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.fastcast.nz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Ftears-of-steel.torrent",
    };

    private TorrentHttpServer? _httpServer;

    public MainWindow()
    {
        InitializeComponent();

        // Resolve WebTorrentClient from DI (configured in App.xaml.cs with AsyncFileSystem + SipSorcery)
        _client = App.Services.GetRequiredService<WebTorrentClient>();

        // Restore persisted torrents into the UI
        _ = RestoreAndLogAsync();

        // Start HTTP server for media streaming
        try
        {
            _httpServer = new TorrentHttpServer(_client, 18770);
            _httpServer.Start();
            Log($"HTTP server started: {_httpServer.BaseUrl}");
        }
        catch (Exception ex) { Log($"HTTP server failed: {ex.Message}"); }

        TorrentListView.ItemsSource = _torrents;
        StatusPeerId.Text = _client.PeerId[..Math.Min(16, _client.PeerId.Length)];

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            try { RefreshUI(); }
            catch (Exception ex) { Log($"Refresh error: {ex.Message}"); }
        };
        _refreshTimer.Start();

        // Clean up resources on close
        Closing += (_, _) =>
        {
            _refreshTimer.Stop();
            _httpServer?.Stop();
            foreach (var t in _torrents) _ = t.Torrent.DisposeAsync();
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
                        var torrentBytes = await System.IO.File.ReadAllBytesAsync(arg);
                        AddFromTorrentBytes(torrentBytes);
                        Log($"Opened: {arg}");
                    }
                    catch (Exception ex) { Log($"Failed to open {arg}: {ex.Message}"); }
                }
                else if (arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    AddMagnet(arg, null);
                }
            }
        };
    }

    private void AddFromTorrentBytes(byte[] torrentBytes)
    {
        var torrent = _client.Add(torrentBytes);
        var hash = torrent.InfoHash ?? "";
        if (_torrents.Any(t => t.HashFull == hash)) return;

        var vm = new TorrentViewModel
        {
            Torrent = torrent,
            Name = torrent.Name ?? hash,
            HashFull = hash,
            HashShort = hash.Length >= 8 ? hash[..8] + "..." : hash,
            SizeText = FormatBytes(torrent.Length),
        };
        if (torrent.Files != null)
        {
            foreach (var f in torrent.Files)
                vm.Files.Add(new FileViewModel { Path = f.Path ?? f.Name ?? "", SizeText = FormatBytes(f.Length), Ext = System.IO.Path.GetExtension(f.Path ?? "").TrimStart('.'), FileIndex = Array.IndexOf(torrent.Files!, f) });
        }

        _torrents.Add(vm);
        TorrentListView.SelectedItem = vm;

        // Web seeds are added automatically from metadata by the Torrent class.
        // Discovery (trackers + WebRTC) is started automatically by InitFromMetadata.

        RegisterTorrentEvents(vm);
    }

    private async Task RestoreAndLogAsync()
    {
        try
        {
            await _client.RestoreFromStorageAsync();
            if (_client.Torrents.Count > 0)
            {
                Dispatcher.Invoke(() =>
                {
                    foreach (var torrent in _client.Torrents)
                    {
                        var hash = torrent.InfoHash ?? "";
                        if (_torrents.Any(t => t.HashFull == hash)) continue;
                        var vm = new TorrentViewModel
                        {
                            Torrent = torrent,
                            Name = torrent.Name ?? hash,
                            HashFull = hash,
                            HashShort = hash.Length >= 8 ? hash[..8] + "..." : hash,
                            SizeText = FormatBytes(torrent.Length),
                        };
                        if (torrent.Files != null)
                            for (int fi = 0; fi < torrent.Files.Length; fi++)
                            {
                                var f = torrent.Files[fi];
                                vm.Files.Add(new FileViewModel { Path = f.Name ?? f.Path ?? "", SizeText = FormatBytes(f.Length), Ext = System.IO.Path.GetExtension(f.Name ?? "").TrimStart('.'), FileIndex = fi });
                            }
                        _torrents.Add(vm);
                        RegisterTorrentEvents(vm);
                        Log($"Restored: {vm.Name} ({torrent.CompletedPieces}/{torrent.PieceCount} pieces)");
                    }
                });
            }
        }
        catch (Exception ex) { Dispatcher.Invoke(() => Log($"Restore failed: {ex.Message}")); }
    }

    /// <summary>Register OnDone event for a torrent view model.</summary>
    private void RegisterTorrentEvents(TorrentViewModel vm)
    {
        vm.Torrent.OnDone += () => Dispatcher.Invoke(() =>
        {
            Log($"[{vm.Name}] Download complete!");
            Title = $"SpawnDev.WebTorrent -- {vm.Name} complete!";
            System.Media.SystemSounds.Asterisk.Play();
            _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.Invoke(() =>
                Title = "SpawnDev.WebTorrent -- Desktop Client"));
        });
    }

    // ── Event Handlers ──

    private void MagnetInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(MagnetInput.Text))
            AddMagnet(MagnetInput.Text.Trim(), null);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(MagnetInput.Text))
            AddMagnet(MagnetInput.Text.Trim(), null);
    }

    private void QuickAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && CCMagnets.TryGetValue(tag, out var magnet))
            AddMagnet(magnet, btn.Content?.ToString());
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

    private void AddMagnet(string magnetUri, string? displayName)
    {
        MagnetInput.Text = "";
        try
        {
            var torrent = _client.Add(magnetUri);
            var hash = torrent.InfoHash ?? "";
            if (_torrents.Any(t => t.HashFull == hash)) { Log($"Already added: {displayName ?? (hash.Length >= 8 ? hash[..8] : hash)}"); return; }

            var vm = new TorrentViewModel
            {
                Torrent = torrent,
                Name = displayName ?? torrent.Name ?? "Loading metadata...",
                HashFull = hash,
                HashShort = hash.Length >= 8 ? hash[..8] + "..." : hash,
            };
            _torrents.Add(vm);
            TorrentListView.SelectedItem = vm;
            Log($"Added: {vm.Name}");

            // When metadata arrives (via ut_metadata or xs= fetch), update the UI
            torrent.OnMetadata += () => Dispatcher.Invoke(() =>
            {
                vm.Name = torrent.Name ?? vm.Name;
                vm.SizeText = FormatBytes(torrent.Length);

                vm.Files.Clear();
                if (torrent.Files != null)
                {
                    foreach (var f in torrent.Files)
                        vm.Files.Add(new FileViewModel { Path = f.Path ?? f.Name ?? "", SizeText = FormatBytes(f.Length), Ext = System.IO.Path.GetExtension(f.Path ?? "").TrimStart('.'), FileIndex = Array.IndexOf(torrent.Files!, f) });
                }

                Log($"[{vm.Name}] {FormatBytes(torrent.Length)}, {torrent.PieceCount} pieces");
            });

            RegisterTorrentEvents(vm);

            // Attempt to fetch .torrent from xs= URL for faster metadata
            _ = TryFetchTorrentFileAsync(torrent, magnetUri);
        }
        catch (Exception ex) { Log($"Error: {ex.Message}"); }
    }

    /// <summary>
    /// Try to fetch .torrent file from the xs= URL in a magnet link.
    /// If successful, sets metadata on the torrent for immediate download.
    /// Discovery (trackers, WebRTC) is handled automatically by the Torrent class.
    /// </summary>
    private async Task TryFetchTorrentFileAsync(Torrent torrent, string magnetUri)
    {
        string? torrentUrl = null;
        foreach (var part in magnetUri.Split('&'))
        {
            var p = part.Contains('?') ? part.Split('?').Last() : part;
            var eq = p.IndexOf('=');
            if (eq < 0) continue;
            var k = p[..eq];
            var v = Uri.UnescapeDataString(p[(eq + 1)..].Replace('+', ' '));
            if (k == "xs") torrentUrl = v;
        }

        if (torrentUrl == null) return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var torrentBytes = await http.GetByteArrayAsync(torrentUrl);
            var metadata = TorrentParser.Parse(torrentBytes);
            if (!string.Equals(metadata.InfoHash, torrent.InfoHash, StringComparison.OrdinalIgnoreCase)) return;

            // SetMetadata is a no-op if metadata was already received via ut_metadata
            torrent.SetMetadata(metadata);
        }
        catch { /* xs= fetch is best-effort — discovery will handle metadata exchange */ }
    }

    // ── UI Updates ──

    private void RefreshUI()
    {
        var totalDown = _torrents.Sum(t => t.Torrent.DownloadSpeed);
        var totalUp = _torrents.Sum(t => t.Torrent.UploadSpeed);
        StatusTorrents.Text = $"{_torrents.Count} torrents";
        StatusPeers.Text = $"{_torrents.Sum(t => t.Torrent.PeerCount)} peers";
        StatusDL.Text = FormatSpeed(totalDown);
        StatusUL.Text = FormatSpeed(totalUp);
        DownSpeedText.Text = FormatSpeed(totalDown);
        UpSpeedText.Text = FormatSpeed(totalUp);

        foreach (var vm in _torrents)
        {
            var t = vm.Torrent;
            vm.ProgressPercent = t.Progress * 100;
            vm.ProgressText = $"{vm.ProgressPercent:F1}%";
            vm.PeerCount = t.PeerCount;
            vm.DownSpeedText = t.DownloadSpeed > 0 ? FormatSpeed(t.DownloadSpeed) : "";
            vm.UpSpeedText = t.UploadSpeed > 0 ? FormatSpeed(t.UploadSpeed) : "";
            vm.StatusText = t.Paused ? "Paused" : t.Done ? "Seeding" : t.CompletedPieces > 0 ? "Downloading" : t.HasMetadata ? "Waiting" : "Metadata";
            // ETA: estimate from download speed
            if (t.Done)
                vm.EtaText = "";
            else if (t.DownloadSpeed > 0 && t.Length > 0)
            {
                var remaining = t.Length - t.Downloaded;
                var etaSec = remaining / t.DownloadSpeed;
                vm.EtaText = etaSec < 60 ? $"{etaSec:F0}s" : etaSec < 3600 ? $"{etaSec / 60:F0}m" : $"{etaSec / 3600:F0}h";
            }
            else
                vm.EtaText = "---";

            // Populate tracker entries (once, when metadata arrives)
            if (vm.TrackerEntries.Count == 0 && t.AnnounceUrls.Length > 0)
            {
                foreach (var url in t.AnnounceUrls)
                    vm.TrackerEntries.Add(new TrackerViewModel { Url = url, Status = "---" });
            }

            // Update tracker peer counts from announce responses
            foreach (var entry in vm.TrackerEntries)
            {
                if (t.TrackerStats.TryGetValue(entry.Url, out var stats))
                    entry.Status = $"{stats.Complete} seeds / {stats.Incomplete} peers";
            }

            // Update file progress
            if (t.Files != null)
            {
                for (int i = 0; i < t.Files.Length && i < vm.Files.Count; i++)
                {
                    var f = t.Files[i];
                    vm.Files[i].ProgressText = $"{f.Progress * 100:F1}%";
                }
            }

            vm.Notify();
        }

        // Refresh filter view when active so torrents move between filter groups automatically
        if (_activeFilter != "all")
            System.Windows.Data.CollectionViewSource.GetDefaultView(_torrents).Refresh();

        // Update the detail panel every refresh cycle (General, Peers, Content, Trackers)
        if (_selectedVm != null)
        {
            UpdateDetailPanel();
            if (_currentTab == "peers")
                UpdatePeersPanel();
        }
    }

    private void UpdateDetailPanel()
    {
        try
        {
        if (_selectedVm == null) return;
        var vm = _selectedVm;
        var t = vm.Torrent;

        DetailName.Text = t.Name ?? vm.Name ?? "---";
        DetailSize.Text = t.HasMetadata ? FormatBytes(t.Length) : "---";
        DetailPieces.Text = t.HasMetadata ? $"{t.CompletedPieces} / {t.PieceCount}" : "---";
        DetailDownloaded.Text = FormatBytes(t.Downloaded);
        DetailUploaded.Text = FormatBytes(t.Uploaded);
        DetailRatio.Text = t.Ratio.ToString("F3");
        DetailEta.Text = vm.EtaText;
        DetailPeerCount.Text = $"{t.PeerCount}";
        DetailHash.Text = vm.HashFull;

        PanelFiles.ItemsSource = vm.Files;
        PanelTrackers.ItemsSource = vm.TrackerEntries;

        UpdateTabVisuals();
        UpdatePieceMap();
        }
        catch { }
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

    }

    private void UpdatePeersPanel()
    {
        if (_selectedVm == null) return;
        var t = _selectedVm.Torrent;
        var peers = t.Wires.ToArray().Select(w =>
        {
            var dl = w.DownloadSpeed();
            var ul = w.UploadSpeed();
            // Web seeds use URL domain as peer ID since they have no real peer ID
            var peerId = w.Type == "webSeed" && w.RemoteAddress != null
                ? (Uri.TryCreate(w.RemoteAddress, UriKind.Absolute, out var uri) ? uri.Host : w.RemoteAddress)
                : w.PeerId;
            return new PeerViewModel
            {
                PeerId = peerId?.Length > 20 ? peerId[..20] + "..." : peerId ?? "---",
                Address = w.RemoteAddress ?? "---",
                Type = w.Type == "webSeed" ? "WebSeed" : "WebRTC",
                DownSpeed = dl > 0 ? FormatSpeed(dl) : "",
                UpSpeed = ul > 0 ? FormatSpeed(ul) : "",
                Progress = w.PeerHasAll ? "100%" : w.PeerPieces != null && w.PeerPieces.Length > 0
                    ? $"{w.PeerPieces.Count(b => b) * 100.0 / w.PeerPieces.Length:F0}%"
                    : "---",
            };
        }).ToList();
        PanelPeers.ItemsSource = peers;
    }

    private static readonly SolidColorBrush PieceEmpty = new(Color.FromRgb(30, 41, 59));
    private static readonly SolidColorBrush PiecePartial = new(Color.FromRgb(59, 130, 246));
    private static readonly SolidColorBrush PieceDone = new(Color.FromRgb(16, 185, 129));
    private int _pieceMapCount;

    private void UpdatePieceMap()
    {
        try
        {
            var bf = _selectedVm?.Torrent.Bitfield;
            if (bf == null || bf.Length == 0) { PieceMapPanel.Children.Clear(); _pieceMapCount = 0; return; }

            int total = bf.Length;
            int cols = Math.Min(total, 2000);
            int step = Math.Max(1, total / cols);

            // Rebuild rectangles only when piece count changes
            if (_pieceMapCount != cols)
            {
                PieceMapPanel.Children.Clear();
                for (int i = 0; i < cols; i++)
                    PieceMapPanel.Children.Add(new Rectangle { Width = 3, Height = 3, Fill = PieceEmpty, Margin = new Thickness(0.25) });
                _pieceMapCount = cols;
            }

            // Update colors only
            for (int i = 0; i < cols && i < PieceMapPanel.Children.Count; i++)
            {
                int start = i * step;
                int end = Math.Min((i + 1) * step, total);
                int done = 0, count = 0;
                for (int j = start; j < end; j++) { count++; if (bf[j]) done++; }

                var brush = done == 0 ? PieceEmpty : done == count ? PieceDone : PiecePartial;
                ((Rectangle)PieceMapPanel.Children[i]).Fill = brush;
            }
        }
        catch { }
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

        var torrent = await _client.SeedAsync(name, data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { "wss://hub.spawndev.com:44365/announce", "wss://tracker.openwebtorrent.com" },
                Comment = "Test torrent from SpawnDev.WebTorrent WPF demo",
            });

        var hash = torrent.InfoHash ?? "";
        var vm = new TorrentViewModel
        {
            Torrent = torrent, Name = name, HashFull = hash,
            HashShort = hash.Length >= 8 ? hash[..8] + "..." : hash,
            SizeText = FormatBytes(data.Length),
        };
        _torrents.Add(vm);
        TorrentListView.SelectedItem = vm;
        RegisterTorrentEvents(vm);

        var magnetUri = torrent.ComputedMagnetUri;
        Log($"Seeding: {name}, magnet: {magnetUri[..Math.Min(60, magnetUri.Length)]}...");
    }

    // ── Pause/Resume/Filter ──
    private string _activeFilter = "all";
    private void PauseAll_Click(object sender, RoutedEventArgs e) { foreach (var t in _torrents) t.Torrent.Pause(); }
    private void ResumeAll_Click(object sender, RoutedEventArgs e) { foreach (var t in _torrents) t.Torrent.Resume(); }
    private void FilterAll_Click(object sender, RoutedEventArgs e) { _activeFilter = "all"; ApplyFilter(); }
    private void FilterDownloading_Click(object sender, RoutedEventArgs e) { _activeFilter = "downloading"; ApplyFilter(); }
    private void FilterSeeding_Click(object sender, RoutedEventArgs e) { _activeFilter = "seeding"; ApplyFilter(); }
    private void FilterPaused_Click(object sender, RoutedEventArgs e) { _activeFilter = "paused"; ApplyFilter(); }

    private void ApplyFilter()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_torrents);
        view.Filter = _activeFilter switch
        {
            "downloading" => o => o is TorrentViewModel vm && !vm.Torrent.Done && vm.Torrent.HasMetadata && !vm.Torrent.Paused,
            "seeding" => o => o is TorrentViewModel vm && vm.Torrent.Done,
            "paused" => o => o is TorrentViewModel vm && vm.Torrent.Paused,
            _ => null,
        };
    }

    // ── Remove / Context Menu ──

    private void TorrentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && TorrentListView.SelectedItem is TorrentViewModel vm)
            RemoveTorrent(vm, deleteData: false);
    }

    private void CtxPauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (TorrentListView.SelectedItem is TorrentViewModel vm)
        {
            if (vm.Torrent.Paused) vm.Torrent.Resume(); else vm.Torrent.Pause();
        }
    }

    private void CtxCopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        if (TorrentListView.SelectedItem is TorrentViewModel vm && !string.IsNullOrEmpty(vm.Torrent.ComputedMagnetUri))
        {
            Clipboard.SetText(vm.Torrent.ComputedMagnetUri);
            Log("Magnet URI copied to clipboard");
        }
    }

    private void CtxRemoveKeep_Click(object sender, RoutedEventArgs e)
    {
        if (TorrentListView.SelectedItem is TorrentViewModel vm)
            RemoveTorrent(vm, deleteData: false);
    }

    private void CtxRemoveDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TorrentListView.SelectedItem is TorrentViewModel vm)
        {
            var result = MessageBox.Show($"Remove '{vm.Name}' and delete all downloaded data?",
                "Remove Torrent", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                RemoveTorrent(vm, deleteData: true);
        }
    }

    private void RemoveTorrent(TorrentViewModel vm, bool deleteData)
    {
        Log($"Removing: {vm.Name}");
        // Remove from UI immediately
        _torrents.Remove(vm);
        if (_selectedVm == vm) _selectedVm = null;
        // Library cleanup in background
        _ = Task.Run(async () =>
        {
            try
            {
                if (deleteData)
                    await _client.RemoveWithDataAsync(vm.Torrent);
                else
                    await _client.RemoveAsync(vm.Torrent);
            }
            catch { }
        });
        Log($"Removed: {vm.Name}");
    }

    // ── File Selection ──

    private void FileSelect_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm == null || sender is not System.Windows.Controls.CheckBox cb || cb.Tag is not FileViewModel fvm) return;
        var torrent = _selectedVm.Torrent;
        if (torrent.Files == null || fvm.FileIndex >= torrent.Files.Length) return;

        var file = torrent.Files[fvm.FileIndex];
        if (fvm.IsSelected)
            file.Select();
        else
            file.Deselect();
    }

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
                AddFromTorrentBytes(torrentBytes);
                Log($"Added via drag-drop: {file}");
            }
            catch (Exception ex) { Log($"Drop error: {ex.Message}"); }
        }
    }

    private void CopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm == null) return;
        var magnet = _selectedVm.Torrent.ComputedMagnetUri;
        Clipboard.SetText(magnet);
        Log("Magnet URI copied to clipboard");
    }

    private void ExportTorrent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm?.Torrent.TorrentFileBytes == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_selectedVm.Name}.torrent",
            Filter = "Torrent files (*.torrent)|*.torrent",
        };
        if (dlg.ShowDialog() == true)
        {
            System.IO.File.WriteAllBytes(dlg.FileName, _selectedVm.Torrent.TorrentFileBytes);
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

        var torrent = await _client.SeedAsync(name, data,
            new TorrentCreatorOptions
            {
                PieceLength = data.Length > 1048576 ? 262144 : 16384,
                Trackers = new[] { "wss://hub.spawndev.com:44365/announce", "wss://tracker.openwebtorrent.com" },
            });

        var hash = torrent.InfoHash ?? "";
        var vm = new TorrentViewModel
        {
            Torrent = torrent, Name = name, HashFull = hash,
            HashShort = hash.Length >= 8 ? hash[..8] + "..." : hash,
            SizeText = FormatBytes(data.Length),
        };
        _torrents.Add(vm);
        TorrentListView.SelectedItem = vm;
        RegisterTorrentEvents(vm);
        Log($"Seeding: {name}, {torrent.PieceCount} pieces");
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
    public Torrent Torrent { get; init; } = null!;
    public string Name { get; set; } = "";
    public string HashFull { get; set; } = "";
    public string HashShort { get; set; } = "";
    public string SizeText { get; set; } = "---";
    public string ProgressText { get; set; } = "0.0%";
    public double ProgressPercent { get; set; }
    public int PeerCount { get; set; }
    public string StatusText { get; set; } = "Metadata";
    public string DownSpeedText { get; set; } = "";
    public string UpSpeedText { get; set; } = "";
    public string EtaText { get; set; } = "";
    public ObservableCollection<FileViewModel> Files { get; } = new();
    public ObservableCollection<TrackerViewModel> TrackerEntries { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressPercent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeerCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownSpeedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpSpeedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EtaText)));
    }
}

public class FileViewModel : INotifyPropertyChanged
{
    private string _progressText = "0%";
    private bool _isSelected = true;
    public string Path { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string Ext { get; set; } = "";
    public int FileIndex { get; set; }
    public string ProgressText { get => _progressText; set { _progressText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText))); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public class TrackerViewModel : INotifyPropertyChanged
{
    private string _status = "";
    public string Url { get; set; } = "";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public class PeerViewModel
{
    public string PeerId { get; set; } = "";
    public string Address { get; set; } = "";
    public string Type { get; set; } = "";
    public string DownSpeed { get; set; } = "";
    public string UpSpeed { get; set; } = "";
    public string Progress { get; set; } = "";
}
