using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bats_Sounds.Models;
using Bats_Sounds.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bats_Sounds.ViewModels;

public record RosterEntry(string Name, string Url);

public partial class MainViewModel : ObservableObject
{
    private readonly RosterService _rosterService;
    private readonly AudioService _audioService;
    private readonly SpotifyService _spotifyService;
    private readonly SpotifyWebService _spotifyWeb;
    private readonly string _playerConfigsDir;
    private readonly string _soundsDir;
    private readonly string _settingsFile;
    private readonly string _featuresFile;
    private readonly string _rostersDir;
    private CancellationTokenSource? _pollCts;

    [ObservableProperty] private ObservableCollection<PlayerViewModel>      _players      = new();
    [ObservableProperty] private ObservableCollection<SoundButtonViewModel>  _soundButtons = new();
    [ObservableProperty] private ObservableCollection<PitcherViewModel>     _pitchers     = new();
    [ObservableProperty] private ObservableCollection<RosterEntry>          _savedRosters = new();
    [ObservableProperty] private RosterEntry? _selectedRoster;
    [ObservableProperty] private bool _isSelectPitcherOpen;
    [ObservableProperty] private bool _isConfirmRemovePitcherOpen;
    [ObservableProperty] private string _confirmRemovePitcherName = string.Empty;
    private PitcherViewModel? _pitcherToRemove;
    [ObservableProperty] private bool _isManageRostersOpen;
    [ObservableProperty] private bool _isUpdatePromptOpen;
    [ObservableProperty] private string _updatePromptVersion = string.Empty;
    [ObservableProperty] private string _newRosterName  = string.Empty;
    [ObservableProperty] private string _newRosterUrl   = string.Empty;
    [ObservableProperty] private RosterEntry? _editingRoster;
    [ObservableProperty] private string _editRosterName = string.Empty;
    [ObservableProperty] private string _editRosterUrl  = string.Empty;

    public bool IsEditingRoster => EditingRoster != null;
    partial void OnEditingRosterChanged(RosterEntry? value) => OnPropertyChanged(nameof(IsEditingRoster));

    // Computed from SelectedRoster — used throughout roster-loading logic
    public string RosterUrl => SelectedRoster?.Url ?? string.Empty;

    partial void OnSelectedRosterChanged(RosterEntry? value)
    {
        OnPropertyChanged(nameof(RosterUrl));
        Players.Clear();
        Pitchers.Clear();
        if (value != null) { LoadCachedRoster(); LoadPitchersFile(); SaveSettings(); }
    }

    [ObservableProperty] private string _statusMessage = "Enter a roster URL and click Load.";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _gridColumns = 4;
    [ObservableProperty] private bool _isSpotifyPlaying;
    [ObservableProperty] private bool _isPlaylistPlaying;
    [ObservableProperty] private bool _isSpotifyAuthenticated;
    [ObservableProperty] private bool _isConnectingSpotify;

    // Playlist display — name is set when user configures a playlist, never overwritten by polling
    [ObservableProperty] private string _savedPlaylistName = string.Empty;
    [ObservableProperty] private string _currentTrackInfo = string.Empty;

    // Change-playlist popup
    [ObservableProperty] private bool _isChangePlaylistOpen;
    [ObservableProperty] private string _changePlaylistUrl = string.Empty;

    // Resume-playlist state
    [ObservableProperty] private bool _hasPlaylistResume;
    [ObservableProperty] private bool _isAnySourcePlaying;
    private string? _resumePlaylistUri;
    private string? _resumeTrackUri;
    private int     _resumePositionMs;
    private bool    _resumePlaylistWasPlaying;

    // Currently active walkup/sound player
    private object? _playingSource; // PlayerViewModel or SoundButtonViewModel
    private DateTime _playingSourceSetAt = DateTime.MinValue;
    // False after PlayTrackAsync (walkup); true after playlist start or pause-resume of playlist
    private bool _spotifyContextIsPlaylist;

    // Overlay info — populated when IsAnySourcePlaying
    [ObservableProperty] private string _playingSourceName = string.Empty;
    [ObservableProperty] private string? _playingSourceImage;
    [ObservableProperty] private string _playingSourceEmoji = string.Empty;
    [ObservableProperty] private string? _playingSourceTrack;
    [ObservableProperty] private string? _playingSourceArtist;

    // ── Generic edit popup ────────────────────────────────────────────────────
    [ObservableProperty] private bool _isEditSoundOpen;
    [ObservableProperty] private string _editSoundTitle = string.Empty;
    [ObservableProperty] private SoundConfig? _editCurrentConfig;
    [ObservableProperty] private bool _editHasExisting;
    [ObservableProperty] private bool _editIsSpotifyMode = true;
    [ObservableProperty] private string _editUri = string.Empty;
    [ObservableProperty] private string _editMinutes = "0";
    [ObservableProperty] private string _editSeconds = "0";
    [ObservableProperty] private string _editMp3File = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _availableMp3Files = new();

    private Action<SoundConfig>? _onEditSave;
    private Action?              _onEditRemove;

    // Saved playlist URL (persisted to settings)
    private string _savedPlaylistUrl = string.Empty;

    public MainViewModel(RosterService rosterService, AudioService audioService,
        SpotifyService spotifyService, SpotifyWebService spotifyWeb,
        string playerConfigsDir, string soundsDir, string appDir)
    {
        _rosterService    = rosterService;
        _audioService     = audioService;
        _spotifyService   = spotifyService;
        _spotifyWeb       = spotifyWeb;
        _playerConfigsDir = playerConfigsDir;
        _soundsDir        = soundsDir;
        _settingsFile = Path.Combine(appDir, "settings.json");
        _featuresFile = Path.Combine(appDir, "features.json");
        _rostersDir   = Path.Combine(appDir, "rosters");
        Directory.CreateDirectory(_rostersDir);

        SoundButtons = new ObservableCollection<SoundButtonViewModel>
        {
            new("game-start",    "GAME START", "▶",  "#1A1A1A"),
            new("coach-meeting", "COACH MTG",  "🤝", "#2A2000"),
            new("homerun",       "HOME RUN",   "⚾", "#C9A84C"),
            new("score",         "SCORE",      "✦",  "#6B5620"),
        };

        LoadFeaturesFile();

        var settings = LoadSettings();
        _savedPlaylistUrl = settings.SpotifyPlaylistUrl;
        SavedPlaylistName = settings.SpotifyPlaylistName;

        LoadRostersIndex(settings.LastRosterUrl);
        SelectedRoster = SavedRosters.FirstOrDefault(r => r.Url == settings.LastRosterUrl)
                      ?? SavedRosters.FirstOrDefault();
        // LoadCachedRoster + LoadPitchersFile are now called from OnSelectedRosterChanged

        IsSpotifyAuthenticated = _spotifyWeb.IsAuthenticated;
        IsSpotifyPlaying       = _spotifyService.IsSpotifyPlaying();

        _audioService.PlaybackStopped += (_, _) =>
        {
            if (_playingSource != null) SetPlayingSource(null);
        };

        if (_spotifyWeb.IsAuthenticated)
        {
            StartPolling();
            _ = TryFetchSavedPlaylistNameAsync();
        }
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private record AppSettings(string LastRosterUrl = "", string SpotifyPlaylistUrl = "", string SpotifyPlaylistName = "");

    private AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return new();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile)) ?? new();
        }
        catch { return new(); }
    }

    private void SaveSettings()
    {
        try { File.WriteAllText(_settingsFile, JsonSerializer.Serialize(new AppSettings(RosterUrl, _savedPlaylistUrl, SavedPlaylistName))); }
        catch { }
    }

    // ── Roster index ─────────────────────────────────────────────────────────

    private string RostersIndexFile => Path.Combine(_rostersDir, "index.json");

    private void LoadRostersIndex(string lastRosterUrl)
    {
        if (File.Exists(RostersIndexFile))
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<RosterEntry>>(File.ReadAllText(RostersIndexFile));
                if (entries != null)
                    foreach (var e in entries)
                        SavedRosters.Add(e);
            }
            catch { }
        }
        else if (!string.IsNullOrWhiteSpace(lastRosterUrl))
        {
            // Migrate: create index from the previously saved URL
            SavedRosters.Add(new RosterEntry("Roster", lastRosterUrl));
            SaveRostersIndex();
        }
    }

    private void SaveRostersIndex()
    {
        try { File.WriteAllText(RostersIndexFile, JsonSerializer.Serialize(SavedRosters.ToList())); }
        catch { }
    }

    [RelayCommand]
    private void OpenManageRosters() => IsManageRostersOpen = true;

    [RelayCommand]
    private void CloseManageRosters() => IsManageRostersOpen = false;

    [RelayCommand]
    private void AddNewRoster()
    {
        var name = NewRosterName.Trim();
        var url  = NewRosterUrl.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return;
        if (SavedRosters.Any(r => r.Url == url)) return;
        var entry = new RosterEntry(name, url);
        SavedRosters.Add(entry);
        SaveRostersIndex();
        SelectedRoster = entry;
        NewRosterName  = string.Empty;
        NewRosterUrl   = string.Empty;
    }

    [RelayCommand]
    private void DeleteRoster(RosterEntry roster)
    {
        SavedRosters.Remove(roster);
        SaveRostersIndex();
        if (SelectedRoster == roster)
            SelectedRoster = SavedRosters.FirstOrDefault();
        if (EditingRoster == roster) CancelEditRoster();
    }

    [RelayCommand]
    private void BeginEditRoster(RosterEntry roster)
    {
        EditingRoster  = roster;
        EditRosterName = roster.Name;
        EditRosterUrl  = roster.Url;
    }

    [RelayCommand]
    private void CancelEditRoster()
    {
        EditingRoster  = null;
        EditRosterName = string.Empty;
        EditRosterUrl  = string.Empty;
    }

    [RelayCommand]
    private void SaveEditRoster()
    {
        if (EditingRoster == null) return;
        var name = EditRosterName.Trim();
        var url  = EditRosterUrl.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return;
        var updated = new RosterEntry(name, url);
        var idx = SavedRosters.IndexOf(EditingRoster);
        if (idx < 0) return;
        var wasSelected = SelectedRoster == EditingRoster;
        SavedRosters[idx] = updated;
        SaveRostersIndex();
        if (wasSelected) SelectedRoster = updated;
        EditingRoster  = null;
        EditRosterName = string.Empty;
        EditRosterUrl  = string.Empty;
    }

    // ── Roster cache ─────────────────────────────────────────────────────────

    private record SavedPlayer(string Name, int? BirthYear, string? ProfileUrl, string? ImagePath);

    private string RosterCacheFile(string url)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url.Trim())));
        return Path.Combine(_rostersDir, $"{hash}.json");
    }

    private string PitchersCacheFile(string url)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url.Trim())));
        return Path.Combine(_rostersDir, $"{hash}_pitchers.json");
    }

    private void LoadCachedRoster()
    {
        if (string.IsNullOrEmpty(RosterUrl)) return;
        var file = RosterCacheFile(RosterUrl);
        if (!File.Exists(file)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedPlayer>>(File.ReadAllText(file));
            if (saved == null || saved.Count == 0) return;
            foreach (var s in saved)
            {
                var player = new Player
                {
                    Name       = s.Name,
                    BirthYear  = s.BirthYear,
                    ProfileUrl = s.ProfileUrl,
                    ImagePath  = s.ImagePath != null && File.Exists(s.ImagePath) ? s.ImagePath : null,
                };
                Players.Add(new PlayerViewModel(player, _playerConfigsDir));
            }
            StatusMessage = $"{Players.Count} players loaded.";
        }
        catch { }
    }

    private void SaveRosterFile(List<Player> players)
    {
        try
        {
            var saved = players
                .Select(p => new SavedPlayer(p.Name, p.BirthYear, p.ProfileUrl, p.ImagePath))
                .ToList();
            File.WriteAllText(RosterCacheFile(RosterUrl), JsonSerializer.Serialize(saved,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── Roster ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadRosterAsync()
    {
        if (string.IsNullOrWhiteSpace(RosterUrl)) { StatusMessage = "Please enter a roster URL."; return; }

        // Cache exists for this URL → load from disk, no network
        if (File.Exists(RosterCacheFile(RosterUrl)))
        {
            Players.Clear();
            LoadCachedRoster();
            LoadPitchersFile();
            SaveSettings();
            return;
        }

        await FetchRosterFromUrlAsync();
    }

    [RelayCommand]
    private async Task RefreshRosterAsync()
    {
        if (string.IsNullOrWhiteSpace(RosterUrl)) { StatusMessage = "Please enter a roster URL."; return; }
        await FetchRosterFromUrlAsync();
    }

    private async Task FetchRosterFromUrlAsync()
    {
        IsLoading = true;
        StatusMessage = "Fetching roster...";
        Players.Clear();

        var (players, error) = await _rosterService.FetchRosterAsync(RosterUrl);
        if (error != null) { StatusMessage = error; IsLoading = false; return; }

        foreach (var p in players)
            Players.Add(new PlayerViewModel(p, _playerConfigsDir));

        LoadPitchersFile();
        SaveSettings();
        StatusMessage = $"Loaded {players.Count} players. Downloading images...";

        var progress = new Progress<string>(name =>
        {
            var vm     = Players.FirstOrDefault(p => p.Name == name);
            var player = players.FirstOrDefault(p => p.Name == name);
            if (vm != null && player?.ImagePath != null) vm.UpdateImagePath(player.ImagePath);
        });

        await _rosterService.DownloadPlayerImagesAsync(players, progress);

        foreach (var p in players)
        {
            var vm = Players.FirstOrDefault(v => v.Name == p.Name);
            if (vm != null && p.ImagePath != null) vm.UpdateImagePath(p.ImagePath);
        }

        // Keep pitcher image paths in sync with newly downloaded player images
        foreach (var pitcher in Pitchers)
        {
            var match = players.FirstOrDefault(p => p.Name == pitcher.Name && p.BirthYear == pitcher.BirthYear);
            if (match?.ImagePath != null) pitcher.UpdateImagePath(match.ImagePath);
        }
        SavePitchersFile();

        SaveRosterFile(players);
        IsLoading = false;
        StatusMessage = $"{players.Count} players loaded.";
    }

    // ── Play player ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PlayPlayerAsync(PlayerViewModel player)
    {
        if (player.Config == null)
        {
            StatusMessage = $"No sound set for {player.Name}. Click ✎ to configure.";
            return;
        }

        await TryCapturePlaylistPositionAsync();

        if (player.Config.Mp3FileName != null)
        {
            var path = Path.Combine(_soundsDir, player.Config.Mp3FileName);
            if (!File.Exists(path)) { StatusMessage = $"{player.Config.Mp3FileName} not found in sounds folder."; return; }
            await PauseSpotifyAsync();
            _audioService.Play(path);
            SetPlayingSource(player);
            StatusMessage = $"♪  {player.Name}";
        }
        else if (player.Config.SpotifyUri != null)
        {
            if (!_spotifyWeb.IsAuthenticated) { StatusMessage = "Connect Spotify first to play walkup tracks."; return; }
            try
            {
                await _spotifyWeb.PlayTrackAsync(player.Config.SpotifyUri, player.Config.StartPositionMs);
                SetPlayingSource(player);
                IsSpotifyPlaying          = true;
                IsPlaylistPlaying         = false;
                _spotifyContextIsPlaylist = false;
                StatusMessage = $"♪  {player.Name}";
            }
            catch (Exception ex) { StatusMessage = $"Spotify error: {ex.Message}"; }
        }
    }

    // ── Play sound button ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PlaySoundButtonAsync(SoundButtonViewModel btn)
    {
        if (!btn.HasConfig)
        {
            StatusMessage = $"No sound for {btn.Label}. Click ✎ to configure.";
            return;
        }

        await TryCapturePlaylistPositionAsync();

        if (btn.Config!.Mp3FileName != null)
        {
            var path = Path.Combine(_soundsDir, btn.Config.Mp3FileName);
            if (!File.Exists(path)) { StatusMessage = $"{btn.Config.Mp3FileName} not found in sounds folder."; return; }
            await PauseSpotifyAsync();
            _audioService.Play(path);
            SetPlayingSource(btn);
            StatusMessage = $"{btn.Emoji}  {btn.Label}!";
        }
        else if (btn.Config.SpotifyUri != null)
        {
            if (!_spotifyWeb.IsAuthenticated) { StatusMessage = "Connect Spotify first."; return; }
            try
            {
                await _spotifyWeb.PlayTrackAsync(btn.Config.SpotifyUri, btn.Config.StartPositionMs);
                SetPlayingSource(btn);
                IsSpotifyPlaying          = true;
                IsPlaylistPlaying         = false;
                _spotifyContextIsPlaylist = false;
                StatusMessage = $"{btn.Emoji}  {btn.Label}!";
            }
            catch (Exception ex) { StatusMessage = $"Spotify error: {ex.Message}"; }
        }
    }

    [RelayCommand]
    private async Task StopCurrentSoundAsync()
    {
        _audioService.Stop();
        if (_spotifyWeb.IsAuthenticated && IsSpotifyPlaying)
        {
            try { await _spotifyWeb.PauseAsync(); } catch { }
        }
        SetPlayingSource(null);
        IsSpotifyPlaying  = false;
        IsPlaylistPlaying = false;
        StatusMessage = HasPlaylistResume ? "Stopped — press Play to resume playlist." : "Stopped.";
    }

    // ── Spotify ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectSpotifyAsync()
    {
        IsConnectingSpotify = true;
        StatusMessage = "Opening browser for Spotify login...";
        try
        {
            await _spotifyWeb.AuthenticateAsync();
            IsSpotifyAuthenticated = true;
            await RefreshPlaybackAsync();
            StartPolling();
            _ = TryFetchSavedPlaylistNameAsync();
            StatusMessage = "Spotify connected.";
        }
        catch (OperationCanceledException) { StatusMessage = "Spotify login timed out."; }
        catch (Exception ex)               { StatusMessage = $"Spotify login failed: {ex.Message}"; }
        finally { IsConnectingSpotify = false; }
    }

    [RelayCommand]
    private async Task ToggleSpotifyAsync()
    {
        try
        {
            if (_spotifyWeb.IsAuthenticated)
            {
                if (IsSpotifyPlaying)
                {
                    await _spotifyWeb.PauseAsync();
                    IsSpotifyPlaying  = false;
                    IsPlaylistPlaying = false;
                    SetPlayingSource(null);
                    StatusMessage = "Spotify: paused";
                }
                else if (HasPlaylistResume && _resumePlaylistUri != null)
                {
                    // Resuming after a walkup interruption
                    if (_resumePlaylistWasPlaying)
                    {
                        // Playlist was mid-play → restart it (Spotify picks up from its context state)
                        await _spotifyWeb.PlayPlaylistAsync(_resumePlaylistUri);
                    }
                    else
                    {
                        // Playlist was paused → resume at exact saved position
                        await _spotifyWeb.ResumeContextAsync(_resumePlaylistUri, _resumeTrackUri!, _resumePositionMs);
                    }
                    HasPlaylistResume         = false;
                    IsSpotifyPlaying          = true;
                    IsPlaylistPlaying         = true;
                    _spotifyContextIsPlaylist = true;
                    StatusMessage = "Spotify: playing";
                    await RefreshPlaybackAsync();
                }
                else
                {
                    // If Spotify's context is a walkup track (not the playlist), start the
                    // configured playlist instead of resuming the walkup.
                    if (!_spotifyContextIsPlaylist && !string.IsNullOrEmpty(_savedPlaylistUrl))
                        await _spotifyWeb.PlayPlaylistAsync(_savedPlaylistUrl);
                    else
                        await _spotifyWeb.ResumeAsync();
                    IsSpotifyPlaying          = true;
                    IsPlaylistPlaying         = true;
                    _spotifyContextIsPlaylist = true;
                    StatusMessage = "Spotify: playing";
                }
            }
            else
            {
                bool wasPlaying = _spotifyService.IsSpotifyPlaying();
                _spotifyService.TogglePlayPause();
                IsSpotifyPlaying  = !wasPlaying;
                IsPlaylistPlaying = IsSpotifyPlaying;
                StatusMessage = wasPlaying ? "Spotify: paused" : "Spotify: playing";
            }
        }
        catch (Exception ex) { StatusMessage = $"Spotify error: {ex.Message}"; }
    }

    // ── Playlist popup ────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenChangePlaylist()
    {
        ChangePlaylistUrl    = _savedPlaylistUrl;
        IsChangePlaylistOpen = true;
    }

    [RelayCommand]
    private void CloseChangePlaylist() => IsChangePlaylistOpen = false;

    [RelayCommand]
    private async Task ConfirmChangePlaylistAsync()
    {
        if (string.IsNullOrWhiteSpace(ChangePlaylistUrl))
        {
            StatusMessage = "Enter a Spotify playlist URL or URI first.";
            return;
        }
        try
        {
            await _spotifyWeb.PlayPlaylistAsync(ChangePlaylistUrl);
            _savedPlaylistUrl         = ChangePlaylistUrl;
            IsSpotifyPlaying          = true;
            IsPlaylistPlaying         = true;
            _spotifyContextIsPlaylist = true;
            IsChangePlaylistOpen      = false;
            SetPlayingSource(null);

            var name = await _spotifyWeb.GetPlaylistNameAsync(ChangePlaylistUrl);
            if (name != null) SavedPlaylistName = name;

            SaveSettings();
            await RefreshPlaybackAsync();
            StatusMessage = "Spotify: playlist started.";
        }
        catch (Exception ex) { StatusMessage = $"Spotify error: {ex.Message}"; }
    }

    // ── Pitchers ──────────────────────────────────────────────────────────────

    private record SavedPitcher(string Name, int? BirthYear, string? ImagePath);

    private void LoadPitchersFile()
    {
        Pitchers.Clear();
        if (string.IsNullOrEmpty(RosterUrl)) return;
        var file = PitchersCacheFile(RosterUrl);
        if (!File.Exists(file)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedPitcher>>(File.ReadAllText(file));
            if (saved == null) return;
            foreach (var s in saved)
            {
                // Prefer image from currently loaded Players (may be fresher than stored path)
                var playerMatch = Players.FirstOrDefault(p => p.Name == s.Name && p.BirthYear == s.BirthYear);
                var imagePath = playerMatch?.ImagePath
                    ?? (s.ImagePath != null && File.Exists(s.ImagePath) ? s.ImagePath : null);
                Pitchers.Add(new PitcherViewModel(s.Name, s.BirthYear, imagePath, _playerConfigsDir));
            }
        }
        catch { }
    }

    private void SavePitchersFile()
    {
        if (string.IsNullOrEmpty(RosterUrl)) return;
        try
        {
            var saved = Pitchers.Select(p => new SavedPitcher(p.Name, p.BirthYear, p.ImagePath)).ToList();
            File.WriteAllText(PitchersCacheFile(RosterUrl), JsonSerializer.Serialize(saved));
        }
        catch { }
    }

    [RelayCommand]
    private void OpenSelectPitcher() => IsSelectPitcherOpen = true;

    [RelayCommand]
    private void CloseSelectPitcher() => IsSelectPitcherOpen = false;

    [RelayCommand]
    private void AddPitcher(PlayerViewModel player)
    {
        if (Pitchers.Any(p => p.Name == player.Name && p.BirthYear == player.BirthYear)) return;
        Pitchers.Add(new PitcherViewModel(player.Name, player.BirthYear, player.ImagePath, _playerConfigsDir));
        SavePitchersFile();
        IsSelectPitcherOpen = false;
    }

    [RelayCommand]
    private void RemovePitcher(PitcherViewModel pitcher)
    {
        _pitcherToRemove              = pitcher;
        ConfirmRemovePitcherName      = pitcher.Name;
        IsConfirmRemovePitcherOpen    = true;
    }

    [RelayCommand]
    private void ConfirmRemovePitcher()
    {
        if (_pitcherToRemove != null)
        {
            Pitchers.Remove(_pitcherToRemove);
            SavePitchersFile();
            _pitcherToRemove = null;
        }
        IsConfirmRemovePitcherOpen = false;
    }

    [RelayCommand]
    private void CancelRemovePitcher()
    {
        _pitcherToRemove           = null;
        IsConfirmRemovePitcherOpen = false;
    }

    [RelayCommand]
    private void OpenEditPitcher(PitcherViewModel pitcher)
        => OpenEditSound($"{pitcher.Name} (pitcher)", pitcher.Config,
            onSave:   config => pitcher.SaveConfig(config),
            onRemove: () => pitcher.ClearConfig());

    [RelayCommand]
    private async Task PlayPitcherAsync(PitcherViewModel pitcher)
    {
        if (pitcher.Config == null)
        {
            StatusMessage = $"No pitcher song for {pitcher.Name}. Click ✎ to configure.";
            return;
        }

        await TryCapturePlaylistPositionAsync();

        if (pitcher.Config.Mp3FileName != null)
        {
            var path = Path.Combine(_soundsDir, pitcher.Config.Mp3FileName);
            if (!File.Exists(path)) { StatusMessage = $"{pitcher.Config.Mp3FileName} not found in sounds folder."; return; }
            await PauseSpotifyAsync();
            _audioService.Play(path);
            SetPlayingSource(pitcher);
            StatusMessage = $"⚾  {pitcher.Name}";
        }
        else if (pitcher.Config.SpotifyUri != null)
        {
            if (!_spotifyWeb.IsAuthenticated) { StatusMessage = "Connect Spotify first to play walkup tracks."; return; }
            try
            {
                await _spotifyWeb.PlayTrackAsync(pitcher.Config.SpotifyUri, pitcher.Config.StartPositionMs);
                SetPlayingSource(pitcher);
                IsSpotifyPlaying          = true;
                IsPlaylistPlaying         = false;
                _spotifyContextIsPlaylist = false;
                StatusMessage = $"⚾  {pitcher.Name}";
            }
            catch (Exception ex) { StatusMessage = $"Spotify error: {ex.Message}"; }
        }
    }

    // ── Generic edit popup ────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenEditPlayer(PlayerViewModel player)
        => OpenEditSound(player.Name, player.Config,
            onSave:   config => player.SaveConfig(config),
            onRemove: () => player.ClearConfig());

    [RelayCommand]
    private void OpenEditSoundButton(SoundButtonViewModel btn)
        => OpenEditSound(btn.Label, btn.Config,
            onSave:   config => { btn.SetConfig(config); SaveFeaturesFile(); },
            onRemove: () =>     { btn.ClearConfig();     SaveFeaturesFile(); });

    private void OpenEditSound(string title, SoundConfig? current, Action<SoundConfig> onSave, Action? onRemove)
    {
        EditSoundTitle    = title;
        EditCurrentConfig = current;
        EditHasExisting   = current != null;
        EditIsSpotifyMode = current?.Mp3FileName == null; // default to Spotify unless already MP3
        EditUri           = current?.SpotifyUri  ?? string.Empty;
        EditMinutes       = (current?.StartMinutes ?? 0).ToString();
        EditSeconds       = (current?.StartSeconds ?? 0).ToString();
        EditMp3File       = current?.Mp3FileName  ?? string.Empty;
        _onEditSave       = onSave;
        _onEditRemove     = onRemove;
        RefreshAvailableMp3Files();
        IsEditSoundOpen = true;
    }

    [RelayCommand]
    private void CloseEditSound() => IsEditSoundOpen = false;

    [RelayCommand]
    private async Task SaveEditSoundAsync()
    {
        if (_onEditSave == null) return;

        SoundConfig config;

        if (EditIsSpotifyMode)
        {
            if (string.IsNullOrWhiteSpace(EditUri))
            {
                StatusMessage = "Enter a Spotify track URL or URI.";
                return;
            }
            if (!int.TryParse(EditMinutes, out var mins) || mins < 0) mins = 0;
            if (!int.TryParse(EditSeconds, out var secs) || secs < 0 || secs > 59) secs = 0;

            var uri = NormalizeSpotifyUri(EditUri);
            string? trackName = null, artistName = null;

            if (_spotifyWeb.IsAuthenticated)
            {
                try
                {
                    StatusMessage = "Fetching track info...";
                    (trackName, artistName) = await _spotifyWeb.GetTrackInfoAsync(uri);
                }
                catch { }
            }

            config = new SoundConfig(SpotifyUri: uri, StartMinutes: mins, StartSeconds: secs,
                                     TrackName: trackName, ArtistName: artistName);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(EditMp3File))
            {
                StatusMessage = "Select an MP3 file from the list.";
                return;
            }
            config = new SoundConfig(Mp3FileName: EditMp3File);
        }

        _onEditSave(config);
        IsEditSoundOpen = false;
        StatusMessage = $"Saved sound for {EditSoundTitle}.";
    }

    [RelayCommand]
    private void RemoveEditSound()
    {
        _onEditRemove?.Invoke();
        IsEditSoundOpen = false;
        StatusMessage = $"Removed sound for {EditSoundTitle}.";
    }

    [RelayCommand]
    private void SetSpotifyMode() => EditIsSpotifyMode = true;

    [RelayCommand]
    private void SetMp3Mode() => EditIsSpotifyMode = false;

    private void RefreshAvailableMp3Files()
    {
        AvailableMp3Files.Clear();
        if (!Directory.Exists(_soundsDir)) return;
        foreach (var f in Directory.GetFiles(_soundsDir, "*.mp3").OrderBy(f => f))
            AvailableMp3Files.Add(Path.GetFileName(f));
    }

    // ── Playback info / polling ───────────────────────────────────────────────

    private async Task RefreshPlaybackAsync()
    {
        try
        {
            var info = await _spotifyWeb.GetCurrentPlaybackAsync();
            IsSpotifyPlaying = info?.IsPlaying ?? false;

            // Auto-clear playing source when their Spotify track ends or changes.
            // MP3 sources (activeUri == null) are cleared by AudioService.PlaybackStopped — skip here.
            // For Spotify sources, wait 4 s after playback starts before checking, to allow
            // Spotify's state to propagate (avoids false-clearing due to API lag).
            if (_playingSource != null && info != null)
            {
                var activeUri = _playingSource is PlayerViewModel pvm       ? pvm.Config?.SpotifyUri
                              : _playingSource is PitcherViewModel pitcher   ? pitcher.Config?.SpotifyUri
                              : _playingSource is SoundButtonViewModel sbvm  ? sbvm.Config?.SpotifyUri
                              : null;
                if (activeUri != null && (DateTime.UtcNow - _playingSourceSetAt).TotalSeconds > 4)
                {
                    bool stillOn = info.TrackUri == activeUri && info.IsPlaying;
                    if (!stillOn) SetPlayingSource(null);
                }
            }

            IsPlaylistPlaying = IsSpotifyPlaying && _playingSource == null;

            // Update the saved playlist name from the live playback context whenever the
            // playlist is active — this is the most reliable source for the name
            if (IsPlaylistPlaying && info?.PlaylistName != null && SavedPlaylistName != info.PlaylistName)
            {
                SavedPlaylistName = info.PlaylistName;
                SaveSettings();
            }

            CurrentTrackInfo = IsPlaylistPlaying && info?.TrackName != null
                ? (info.ArtistName != null ? $"{info.TrackName} — {info.ArtistName}" : info.TrackName)
                : string.Empty;
        }
        catch { }
    }

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        timer.Tick += async (_, _) =>
        {
            if (_pollCts.IsCancellationRequested) { timer.Stop(); return; }
            await RefreshPlaybackAsync();
        };
        timer.Start();
    }

    private async Task TryFetchSavedPlaylistNameAsync()
    {
        if (string.IsNullOrEmpty(_savedPlaylistUrl) || !string.IsNullOrEmpty(SavedPlaylistName))
            return;
        try
        {
            var name = await _spotifyWeb.GetPlaylistNameAsync(_savedPlaylistUrl);
            if (name != null) { SavedPlaylistName = name; SaveSettings(); }
        }
        catch { }
    }

    // ── Features file ─────────────────────────────────────────────────────────

    private void LoadFeaturesFile()
    {
        if (!File.Exists(_featuresFile)) return;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, SoundConfig>>(
                File.ReadAllText(_featuresFile));
            if (dict == null) return;
            foreach (var btn in SoundButtons)
                if (dict.TryGetValue(btn.Key, out var cfg))
                    btn.SetConfig(cfg);
        }
        catch { }
    }

    private void SaveFeaturesFile()
    {
        try
        {
            var dict = SoundButtons
                .Where(b => b.Config != null)
                .ToDictionary(b => b.Key, b => b.Config!);
            File.WriteAllText(_featuresFile, JsonSerializer.Serialize(dict,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private void SetPlayingSource(object? source)
    {
        if (_playingSource is PlayerViewModel pvm)       pvm.IsPlaying     = false;
        if (_playingSource is PitcherViewModel pvmP)     pvmP.IsPlaying    = false;
        if (_playingSource is SoundButtonViewModel sbvm) sbvm.IsPlaying    = false;
        _playingSource     = source;
        IsAnySourcePlaying = source != null;
        if (source != null) _playingSourceSetAt = DateTime.UtcNow;
        if (source is PlayerViewModel newPvm)
        {
            newPvm.IsPlaying    = true;
            PlayingSourceName   = newPvm.Name;
            PlayingSourceImage  = newPvm.ImagePath;
            PlayingSourceEmoji  = string.Empty;
            PlayingSourceTrack  = newPvm.Config?.TrackName ?? newPvm.Config?.Mp3FileName;
            PlayingSourceArtist = newPvm.Config?.ArtistName;
        }
        else if (source is PitcherViewModel newPitcher)
        {
            newPitcher.IsPlaying = true;
            PlayingSourceName   = newPitcher.Name;
            PlayingSourceImage  = newPitcher.ImagePath;
            PlayingSourceEmoji  = "⚾";
            PlayingSourceTrack  = newPitcher.Config?.TrackName ?? newPitcher.Config?.Mp3FileName;
            PlayingSourceArtist = newPitcher.Config?.ArtistName;
        }
        else if (source is SoundButtonViewModel newSbvm)
        {
            newSbvm.IsPlaying   = true;
            PlayingSourceName   = newSbvm.Label;
            PlayingSourceImage  = null;
            PlayingSourceEmoji  = newSbvm.Emoji;
            PlayingSourceTrack  = newSbvm.Config?.TrackName ?? newSbvm.Config?.Mp3FileName;
            PlayingSourceArtist = newSbvm.Config?.ArtistName;
        }
        else
        {
            PlayingSourceName   = string.Empty;
            PlayingSourceImage  = null;
            PlayingSourceEmoji  = string.Empty;
            PlayingSourceTrack  = null;
            PlayingSourceArtist = null;
        }
    }

    private async Task TryCapturePlaylistPositionAsync()
    {
        try
        {
            if (!_spotifyWeb.IsAuthenticated) return;
            var current = await _spotifyWeb.GetCurrentPlaybackAsync();
            if (current?.PlaylistUri != null && current.TrackUri != null)
            {
                _resumePlaylistUri        = current.PlaylistUri;
                _resumeTrackUri           = current.TrackUri;
                _resumePositionMs         = current.ProgressMs;
                _resumePlaylistWasPlaying = current.IsPlaying;
                HasPlaylistResume         = true;
            }
        }
        catch { }
    }

    private async Task PauseSpotifyAsync()
    {
        try
        {
            if (_spotifyWeb.IsAuthenticated && IsSpotifyPlaying)
                await _spotifyWeb.PauseAsync();
            else
                _spotifyService.Pause();
        }
        catch { _spotifyService.Pause(); }
        SetPlayingSource(null);
        IsSpotifyPlaying  = false;
        IsPlaylistPlaying = false;
        CurrentTrackInfo  = string.Empty;
    }

    private static string NormalizeSpotifyUri(string input)
    {
        input = input.Trim();
        if (input.StartsWith("spotify:")) return input;
        if (input.Contains("open.spotify.com/"))
        {
            var path = new Uri(input).AbsolutePath.TrimStart('/');
            return "spotify:" + path.Replace('/', ':');
        }
        return $"spotify:track:{input}";
    }
}
