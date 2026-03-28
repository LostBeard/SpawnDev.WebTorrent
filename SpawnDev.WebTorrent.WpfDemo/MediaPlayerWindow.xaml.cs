using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SpawnDev.WebTorrent.WpfDemo;

public partial class MediaPlayerWindow : Window
{
    private readonly DispatcherTimer _posTimer;
    private bool _isPlaying;

    public MediaPlayerWindow(string url, string fileName)
    {
        InitializeComponent();
        FileNameText.Text = fileName;
        Title = $"SpawnDev.WebTorrent — {fileName}";

        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _posTimer.Tick += (_, _) => UpdatePosition();

        Player.Source = new Uri(url);
        Player.Play();
        _isPlaying = true;
        _posTimer.Start();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
            SeekBar.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        MessageBox.Show($"Media playback failed: {e.ErrorException?.Message}", "Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Player.Pause();
            PlayPauseBtn.Content = "Play";
        }
        else
        {
            Player.Play();
            PlayPauseBtn.Content = "Pause";
        }
        _isPlaying = !_isPlaying;
    }

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Player.Position = TimeSpan.FromSeconds(SeekBar.Value);
    }

    private void UpdatePosition()
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        var pos = Player.Position;
        var dur = Player.NaturalDuration.TimeSpan;
        SeekBar.Value = pos.TotalSeconds;
        TimeText.Text = $"{pos:mm\\:ss} / {dur:mm\\:ss}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _posTimer.Stop();
        Player.Stop();
        base.OnClosed(e);
    }
}
