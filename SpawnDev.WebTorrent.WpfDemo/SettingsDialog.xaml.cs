using System.Windows;

namespace SpawnDev.WebTorrent.WpfDemo;

public partial class SettingsDialog : Window
{
    private readonly WebTorrentClient _client;

    public SettingsDialog(WebTorrentClient client)
    {
        InitializeComponent();
        _client = client;
        PeerIdText.Text = client.PeerId[..Math.Min(16, client.PeerId.Length)];
        // TODO: DownloadLimit/UploadLimit not yet ported to _Alt — show placeholder
        DownLimitBox.Text = "0";
        UpLimitBox.Text = "0";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Apply rate limits once DownloadLimit/UploadLimit are ported to _Alt
        Close();
    }
}
