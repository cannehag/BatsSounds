using System.IO;
using System.Text.Json;
using Bats_Sounds.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bats_Sounds.ViewModels;

public partial class PitcherViewModel : ObservableObject
{
    private readonly string _configDir;

    public string  Name      { get; }
    public int?    BirthYear { get; }

    public string  FirstName => Name.Contains(' ') ? Name[..Name.IndexOf(' ')] : Name;
    public string  LastName  => Name.Contains(' ') ? Name[(Name.IndexOf(' ') + 1)..] : string.Empty;

    [ObservableProperty] private string?    _imagePath;
    [ObservableProperty] private SoundConfig? _config;
    [ObservableProperty] private bool       _hasConfig;
    [ObservableProperty] private bool       _isPlaying;

    public string? ConfigSummary =>
        Config?.TrackName ?? (Config?.Mp3FileName != null ? $"📂 {Config.Mp3FileName}" : null);

    public string? StartTimeDisplay =>
        Config == null ? null :
        Config.StartPositionMs == 0 ? "From beginning" :
        $"From {Config.StartMinutes}:{Config.StartSeconds:D2}";

    public PitcherViewModel(string name, int? birthYear, string? imagePath, string configDir)
    {
        _configDir = configDir;
        Name       = name;
        BirthYear  = birthYear;
        ImagePath  = imagePath;
        RefreshConfig();
    }

    public void RefreshConfig()
    {
        var path = ConfigFilePath();
        if (File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<SoundConfig>(File.ReadAllText(path));
                Config    = cfg;
                HasConfig = cfg != null;
                OnPropertyChanged(nameof(ConfigSummary));
                OnPropertyChanged(nameof(StartTimeDisplay));
                return;
            }
            catch { }
        }
        Config    = null;
        HasConfig = false;
        OnPropertyChanged(nameof(ConfigSummary));
        OnPropertyChanged(nameof(StartTimeDisplay));
    }

    public void SaveConfig(SoundConfig config)
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(ConfigFilePath(), JsonSerializer.Serialize(config));
        Config    = config;
        HasConfig = true;
        OnPropertyChanged(nameof(ConfigSummary));
        OnPropertyChanged(nameof(StartTimeDisplay));
    }

    public void ClearConfig()
    {
        var path = ConfigFilePath();
        if (File.Exists(path)) File.Delete(path);
        Config    = null;
        HasConfig = false;
        OnPropertyChanged(nameof(ConfigSummary));
        OnPropertyChanged(nameof(StartTimeDisplay));
    }

    public void UpdateImagePath(string? path) => ImagePath = path;

    private string ConfigFilePath()
    {
        var key = BirthYear.HasValue ? $"{SanitizeName(Name)}_{BirthYear}" : SanitizeName(Name);
        return Path.Combine(_configDir, key + "_pitcher.json");
    }

    private static string SanitizeName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
