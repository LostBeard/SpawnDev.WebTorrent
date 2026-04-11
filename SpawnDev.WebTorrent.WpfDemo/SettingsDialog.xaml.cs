using System.Windows;

namespace SpawnDev.WebTorrent.WpfDemo;

public partial class SettingsDialog : Window
{
    private readonly WebTorrentClient _client;

    public SettingsDialog(WebTorrentClient client)
    {
        InitializeComponent();
        _client = client;

        // Transfer
        var dlRate = client.DownloadRateLimiter.Rate;
        var ulRate = client.UploadRateLimiter.Rate;
        DownLimitBox.Text = dlRate <= 0 ? "0" : (dlRate / 1024).ToString("F0");
        UpLimitBox.Text = ulRate <= 0 ? "0" : (ulRate / 1024).ToString("F0");

        // Connections
        MaxConnsBox.Text = client.MaxConns.ToString();
        EnableTrackersCheck.IsChecked = client.EnableTrackers;
        EnableDhtCheck.IsChecked = client.EnableDht;
        EnablePexCheck.IsChecked = client.EnableUtPex;
        EnableLsdCheck.IsChecked = client.EnableLsd;
        EnableWebSeedsCheck.IsChecked = client.EnableWebSeeds;

        // Trackers
        TrackersBox.Text = string.Join("\n", client.DefaultTrackers);

        // Storage
        var storagePath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SpawnDev.WebTorrent");
        StoragePathText.Text = storagePath;

        // Client
        PeerIdText.Text = client.PeerId;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        // Transfer
        if (long.TryParse(DownLimitBox.Text, out var dlKb))
            _client.ThrottleDownload(dlKb <= 0 ? -1 : dlKb * 1024);
        if (long.TryParse(UpLimitBox.Text, out var ulKb))
            _client.ThrottleUpload(ulKb <= 0 ? -1 : ulKb * 1024);

        // Connections
        if (int.TryParse(MaxConnsBox.Text, out var maxConns) && maxConns > 0)
            _client.MaxConns = maxConns;
        _client.EnableTrackers = EnableTrackersCheck.IsChecked == true;
        _client.EnableDht = EnableDhtCheck.IsChecked == true;
        _client.EnableUtPex = EnablePexCheck.IsChecked == true;
        _client.EnableLsd = EnableLsdCheck.IsChecked == true;
        _client.EnableWebSeeds = EnableWebSeedsCheck.IsChecked == true;

        // Trackers
        var trackerLines = TrackersBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _client.DefaultTrackers = trackerLines.Where(t => t.StartsWith("wss://") || t.StartsWith("ws://")).ToArray();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Apply_Click(sender, e);
        Close();
    }
}
