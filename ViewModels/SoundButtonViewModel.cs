using Bats_Sounds.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bats_Sounds.ViewModels;

public partial class SoundButtonViewModel : ObservableObject
{
    public string Key         { get; }
    public string Label       { get; }
    public string Emoji       { get; }
    public string ButtonColor { get; }

    [ObservableProperty] private SoundConfig? _config;
    [ObservableProperty] private bool _hasConfig;
    [ObservableProperty] private bool _isPlaying;

    public string? ConfigSummary =>
        Config?.TrackName
        ?? (Config?.Mp3FileName != null ? $"📂 {Config.Mp3FileName}" : null);

    public SoundButtonViewModel(string key, string label, string emoji, string buttonColor)
    {
        Key         = key;
        Label       = label;
        Emoji       = emoji;
        ButtonColor = buttonColor;
    }

    public void SetConfig(SoundConfig config)
    {
        Config    = config;
        HasConfig = true;
        OnPropertyChanged(nameof(ConfigSummary));
    }

    public void ClearConfig()
    {
        Config    = null;
        HasConfig = false;
        OnPropertyChanged(nameof(ConfigSummary));
    }
}
