using System.IO;
using System.Text.Json;
using Bats_Sounds.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bats_Sounds.ViewModels;

public partial class PlayerViewModel : ObservableObject
{
    private readonly string _configDir;

    [ObservableProperty] private string _name = string.Empty;

    public string FirstName => Name.Contains(' ') ? Name[..Name.IndexOf(' ')] : Name;
    public string LastName  => Name.Contains(' ') ? Name[(Name.IndexOf(' ') + 1)..] : string.Empty;

    public int? BirthYear { get; }

    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private bool _hasConfig;
    [ObservableProperty] private SoundConfig? _config;
    [ObservableProperty] private bool _isPlaying;

    public string? ConfigSummary =>
        Config?.TrackName
        ?? (Config?.Mp3FileName != null ? $"📂 {Config.Mp3FileName}" : null);

    public string? StartTimeDisplay =>
        Config == null ? null :
        Config.StartPositionMs == 0 ? "From beginning" :
        $"From {Config.StartMinutes}:{Config.StartSeconds:D2}";

    public PlayerViewModel(Player player, string configDir)
    {
        _configDir = configDir;
        Name       = player.Name;
        BirthYear  = player.BirthYear;
        ImagePath  = player.ImagePath;
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
        var baseName = SanitizeName(Name);
        var key = BirthYear.HasValue ? $"{baseName}_{BirthYear}" : baseName;
        return Path.Combine(_configDir, key + ".json");
    }

    private static string SanitizeName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
