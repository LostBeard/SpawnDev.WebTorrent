using System.Text;
using System.Windows;

namespace SpawnDev.WebTorrent.WpfDemo;

public partial class SettingsDialog : Window
{
    private readonly WebTorrentClient _client;

    public SettingsDialog(WebTorrentClient client)
    {
        InitializeComponent();
        _client = client;
        PeerIdText.Text = Encoding.ASCII.GetString(client.PeerId, 0, 8);
        DownLimitBox.Text = client.DownloadLimit < 0 ? "0" : (client.DownloadLimit / 1024).ToString();
        UpLimitBox.Text = client.UploadLimit < 0 ? "0" : (client.UploadLimit / 1024).ToString();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(DownLimitBox.Text, out var dl))
            _client.DownloadLimit = dl <= 0 ? -1 : dl * 1024;
        if (int.TryParse(UpLimitBox.Text, out var ul))
            _client.UploadLimit = ul <= 0 ? -1 : ul * 1024;
        Close();
    }
}
