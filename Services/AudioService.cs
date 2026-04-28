using System.Windows.Media;

namespace Bats_Sounds.Services;

public class AudioService
{
    private readonly MediaPlayer _player = new();
    private string? _currentFile;

    public event EventHandler? PlaybackStarted;
    public event EventHandler? PlaybackStopped;

    public AudioService()
    {
        _player.MediaEnded += (_, _) => PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Play(string filePath)
    {
        _player.Stop();
        _player.Open(new Uri(filePath, UriKind.Absolute));
        _player.Play();
        _currentFile = filePath;
        PlaybackStarted?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _player.Stop();
        _currentFile = null;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public string? CurrentFile => _currentFile;
}
