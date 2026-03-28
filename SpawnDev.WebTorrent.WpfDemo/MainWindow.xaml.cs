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

    public MainWindow()
    {
        InitializeComponent();
        _client = new WebTorrentClient();
        TorrentListView.ItemsSource = _torrents;
        StatusPeerId.Text = System.Text.Encoding.ASCII.GetString(_client.PeerId, 0, 8);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            foreach (var t in _torrents) t.Swarm.UpdateSpeed();
            RefreshUI();
        };
        _refreshTimer.Start();

        Log("SpawnDev.WebTorrent Desktop Client initialized");
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

            vm.Swarm.OnDone += () => Dispatcher.Invoke(() => { Log($"[{vm.Name}] Download complete!"); });

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

        // Create SIPSorcery WebRTC transport for desktop P2P
        IWebRtcTransport webRtc = new SipSorceryWebRtcTransport();

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
        StatusTorrents.Text = $"{_torrents.Count} torrents";
        StatusPeers.Text = $"{_torrents.Sum(t => t.Swarm.PeerCount)} peers";
        DownSpeedText.Text = FormatSpeed(_torrents.Sum(t => t.Swarm.DownloadSpeed));
        UpSpeedText.Text = FormatSpeed(_torrents.Sum(t => t.Swarm.UploadSpeed));

        foreach (var vm in _torrents)
        {
            var pm = vm.Swarm.PieceManager;
            vm.ProgressPercent = (pm?.Progress ?? 0) * 100;
            vm.ProgressText = $"{vm.ProgressPercent:F1}%";
            vm.PeerCount = vm.Swarm.PeerCount;
            vm.DownSpeedText = vm.Swarm.DownloadSpeed > 0 ? FormatSpeed(vm.Swarm.DownloadSpeed) : "";
            vm.UpSpeedText = vm.Swarm.UploadSpeed > 0 ? FormatSpeed(vm.Swarm.UploadSpeed) : "";
            vm.StatusText = pm?.IsComplete == true ? "Seeding" : pm != null && pm.CompletedCount > 0 ? "Downloading" : vm.Swarm.HasMetadata ? "Waiting" : "Metadata";
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
        TabFiles.Foreground = _currentTab == "files" ? (Brush)FindResource("AccentGreen") : (Brush)FindResource("TextMuted");
        TabFiles.BorderBrush = _currentTab == "files" ? (Brush)FindResource("AccentGreen") : Brushes.Transparent;
        TabTrackers.Foreground = _currentTab == "trackers" ? (Brush)FindResource("AccentGreen") : (Brush)FindResource("TextMuted");
        TabTrackers.BorderBrush = _currentTab == "trackers" ? (Brush)FindResource("AccentGreen") : Brushes.Transparent;
        TabLog.Foreground = _currentTab == "log" ? (Brush)FindResource("AccentGreen") : (Brush)FindResource("TextMuted");
        TabLog.BorderBrush = _currentTab == "log" ? (Brush)FindResource("AccentGreen") : Brushes.Transparent;

        PanelGeneral.Visibility = _currentTab == "general" ? Visibility.Visible : Visibility.Collapsed;
        PanelFiles.Visibility = _currentTab == "files" ? Visibility.Visible : Visibility.Collapsed;
        PanelTrackers.Visibility = _currentTab == "trackers" ? Visibility.Visible : Visibility.Collapsed;
        PanelLog.Visibility = _currentTab == "log" ? Visibility.Visible : Visibility.Collapsed;
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
}

public class TrackerViewModel : INotifyPropertyChanged
{
    private string _status = "";
    public string Url { get; set; } = "";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
