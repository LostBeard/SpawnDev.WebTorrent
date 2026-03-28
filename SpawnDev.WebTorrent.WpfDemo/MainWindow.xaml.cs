using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
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
        TorrentList.ItemsSource = _torrents;
        PeerIdText.Text = System.Text.Encoding.ASCII.GetString(_client.PeerId, 0, 8);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshUI();
        _refreshTimer.Start();

        Log("Client initialized. Peer ID: " + PeerIdText.Text);
    }

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
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag && CCMagnets.TryGetValue(tag, out var magnet))
        {
            var name = btn.Content?.ToString();
            _ = AddMagnetAsync(magnet, name);
        }
    }

    private void RemoveTorrent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string hash)
        {
            var entry = _torrents.FirstOrDefault(t => t.HashFull == hash);
            if (entry != null)
            {
                entry.Coordinator?.Stop();
                _torrents.Remove(entry);
                Log($"Removed: {entry.Name}");
                RefreshUI();
            }
        }
    }

    private async Task AddMagnetAsync(string magnetUri, string? displayName)
    {
        MagnetInput.Text = "";

        try
        {
            var swarm = await _client.AddAsync(magnetUri);
            var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();

            if (_torrents.Any(t => t.HashFull == hash))
            {
                Log($"Already added: {displayName ?? hash[..8]}");
                return;
            }

            var vm = new TorrentViewModel
            {
                Swarm = swarm,
                Name = displayName ?? "Loading metadata...",
                HashFull = hash,
                HashShort = hash[..8] + "...",
            };

            _torrents.Add(vm);
            Log($"Added: {vm.Name}");
            RefreshUI();

            // Fetch metadata and start download
            await FetchMetadataAndDownloadAsync(vm, magnetUri);
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
    }

    private async Task FetchMetadataAndDownloadAsync(TorrentViewModel vm, string magnetUri)
    {
        string? torrentUrl = null;
        var webSeedUrls = new List<string>();

        foreach (var part in magnetUri.Split('&'))
        {
            var p = part.Contains('?') ? part.Split('?').Last() : part;
            var eqIdx = p.IndexOf('=');
            if (eqIdx < 0) continue;
            var key = p[..eqIdx];
            var val = Uri.UnescapeDataString(p[(eqIdx + 1)..].Replace('+', ' '));
            if (key == "xs") torrentUrl = val;
            if (key == "ws") webSeedUrls.Add(val);
        }

        if (torrentUrl == null)
        {
            Log($"[{vm.Name}] No .torrent URL — waiting for peers");
            return;
        }

        try
        {
            Log($"[{vm.Name}] Fetching .torrent...");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var torrentBytes = await http.GetByteArrayAsync(torrentUrl);
            var metadata = TorrentParser.Parse(torrentBytes);

            if (!metadata.InfoHash.SequenceEqual(vm.Swarm.InfoHash))
            {
                Log($"[{vm.Name}] Info hash mismatch!");
                return;
            }

            foreach (var ws in metadata.UrlList)
                if (!webSeedUrls.Contains(ws)) webSeedUrls.Add(ws);

            vm.Swarm.SetMetadata(metadata);
            vm.Name = metadata.Name;
            vm.SizeText = FormatBytes(metadata.TotalLength);
            vm.PiecesText = $"{metadata.PieceCount} pcs";
            vm.Files.Clear();
            foreach (var f in metadata.Files)
                vm.Files.Add(new FileViewModel { Path = f.Path, SizeText = FormatBytes(f.Length) });

            Log($"[{vm.Name}] {metadata.Files.Length} file(s), {FormatBytes(metadata.TotalLength)}, {metadata.PieceCount} pieces");

            var store = new MemoryChunkStore(metadata.PieceLength);
            var pm = new PieceManager(metadata, store);
            var coordinator = new DownloadCoordinator(pm, metadata);
            vm.PieceManager = pm;
            vm.Coordinator = coordinator;

            foreach (var wsUrl in webSeedUrls)
            {
                coordinator.AddWebSeed(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, wsUrl);
                Log($"[{vm.Name}] Web seed: {wsUrl}");
            }

            coordinator.OnPieceComplete += (idx) => Dispatcher.Invoke(RefreshUI);
            coordinator.OnDownloadComplete += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    Log($"[{vm.Name}] Download complete!");
                    RefreshUI();
                });
            };

            coordinator.Start();
            Log($"[{vm.Name}] Download started ({webSeedUrls.Count} web seed(s))");
            RefreshUI();
        }
        catch (Exception ex)
        {
            Log($"[{vm.Name}] Error: {ex.Message}");
        }
    }

    private void RefreshUI()
    {
        TorrentCountText.Text = _torrents.Count.ToString();
        foreach (var vm in _torrents)
        {
            var progress = vm.PieceManager?.Progress ?? 0;
            var completed = vm.PieceManager?.CompletedCount ?? 0;
            var total = vm.Swarm.Metadata?.PieceCount ?? 0;
            vm.ProgressText = $"{(progress * 100):F1}% — {completed}/{total} pieces";
            vm.ProgressWidth = Math.Max(0, progress * 830);
            vm.StatusText = vm.PieceManager?.IsComplete == true ? "  COMPLETE" : "";
            vm.FilesVisibility = vm.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            vm.OnPropertyChanged(nameof(TorrentViewModel.ProgressText));
            vm.OnPropertyChanged(nameof(TorrentViewModel.ProgressWidth));
            vm.OnPropertyChanged(nameof(TorrentViewModel.StatusText));
            vm.OnPropertyChanged(nameof(TorrentViewModel.SizeText));
            vm.OnPropertyChanged(nameof(TorrentViewModel.PiecesText));
            vm.OnPropertyChanged(nameof(TorrentViewModel.Name));
            vm.OnPropertyChanged(nameof(TorrentViewModel.FilesVisibility));
        }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        LogText.Text += line;
        LogScroller.ScrollToEnd();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1073741824) return $"{bytes / 1048576.0:F1} MB";
        return $"{bytes / 1073741824.0:F2} GB";
    }
}

public class TorrentViewModel : INotifyPropertyChanged
{
    public TorrentSwarm Swarm { get; init; } = null!;
    public string Name { get; set; } = "";
    public string HashFull { get; set; } = "";
    public string HashShort { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string PiecesText { get; set; } = "";
    public string ProgressText { get; set; } = "0.0%";
    public string StatusText { get; set; } = "";
    public double ProgressWidth { get; set; }
    public Visibility FilesVisibility { get; set; } = Visibility.Collapsed;
    public ObservableCollection<FileViewModel> Files { get; } = new();
    public PieceManager? PieceManager { get; set; }
    public DownloadCoordinator? Coordinator { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class FileViewModel
{
    public string Path { get; set; } = "";
    public string SizeText { get; set; } = "";
}
