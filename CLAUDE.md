# Bats - Sound Control — Claude Context

## What this is
WPF .NET 10 app for live baseball game audio control. Runs on a laptop at the stadium. Lets the operator load a team roster, assign Spotify tracks or MP3s to each player as walkup songs, and play them one-click. Also has sidebar sound buttons (game start, homerun, score, coach meeting) and Spotify playlist control.

## Tech stack
- .NET 10, WPF, C# 13
- CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`, `ObservableObject`)
- HtmlAgilityPack — roster scraping
- Spotify Web API (PKCE OAuth, no secret) — `SpotifyWebService.cs`
- `System.Windows.Media.MediaPlayer` via `AudioService` — local MP3 playback
- Win32 media key P/Invoke via `SpotifyService` — fallback Spotify toggle when not authenticated

## Source file map
```
Bats Sounds.csproj          net10.0-windows, HtmlAgilityPack, CommunityToolkit.Mvvm
App.xaml / App.xaml.cs      Standard WPF entry point
MainWindow.xaml             All UI — dark theme, 4-row grid layout
MainWindow.xaml.cs          Code-behind: layout calc + SelectPitcherList_SelectionChanged + TimeField_GotFocus

Models/
  Player.cs                 { Name, BirthYear?, ProfileUrl?, ImagePath? }
  PlayerConfig.cs           SoundConfig record: SpotifyUri | Mp3FileName, start time, track/artist names

ViewModels/
  MainViewModel.cs          Central VM — all commands, state, Spotify polling, pitch/batter management
  PlayerViewModel.cs        Wraps Player; config file = {SanitizedName}[_{BirthYear}].json
  PitcherViewModel.cs       Same as PlayerViewModel but config file suffix = _pitcher.json
  SoundButtonViewModel.cs   Sidebar sound buttons (game-start, homerun, score, coach-meeting)

Services/
  RosterService.cs          FetchRosterAsync (HtmlAgilityPack scraper) + DownloadPlayerImagesAsync
  AudioService.cs           MediaPlayer wrapper; fires PlaybackStopped event
  SpotifyWebService.cs      Full Spotify Web API: PKCE auth, play/pause/resume, polling
  SpotifyService.cs         Win32 media key fallback (VK_MEDIA_PLAY_PAUSE)

Controls/
  EqualizerBars.xaml/.cs    4-bar animated equalizer UserControl; DependencyProperty IsAnimating

Converters/
  BoolToColorConverter.cs   Generic bool→value converter; used as InverseBoolToVis in XAML
```

## Runtime directories (created at startup next to .exe)
```
images/         Downloaded player photos ({Name}[_{BirthYear}].jpg)
players/        Per-player sound configs ({Name}[_{BirthYear}].json and _pitcher.json)
sounds/         User-placed MP3 files for sound buttons and walkups
rosters/        Per-URL roster cache: {MD5(url)}.json
pitchers.json   List of saved pitchers (name, birthYear, imagePath)
settings.json   Last roster URL, Spotify playlist URL + name
features.json   Sound button configs keyed by button key
spotify_tokens.json  Spotify PKCE refresh/access tokens + scopes + expiry
```

## Layout: MainWindow.xaml grid
```
Row 0: Top bar (roster URL TextBox + Load + Refresh buttons)
Row 1: DockPanel → "BATTERS" gold header + ListBox (UniformGrid of player cards)
Row 2: Pitcher panel (horizontal ScrollViewer of pitcher cards + Add card)
Row 3: Status bar
Col 1: Sidebar (RowSpan=4) — branding, sound buttons, Spotify controls
Overlays (ZIndex 20-30, RowSpan=4, ColSpan=2):
  - Edit Sound popup (ZIndex 20)
  - Change Playlist popup (ZIndex 20)
  - Select Pitcher popup (ZIndex 20)
  - Confirm Remove Pitcher popup (ZIndex 25)
  - Playing overlay (ZIndex 30) — shown while IsAnySourcePlaying
```

## Key design decisions

**Roster caching**: `LoadRosterAsync` checks `rosters/{MD5(url)}.json` — if it exists, loads from disk instantly. `RefreshRosterAsync` (↻ button) always re-fetches from URL. Multiple URLs each get their own cache file.

**Batter vs pitcher songs**: `PlayerViewModel` config = `{name}.json`; `PitcherViewModel` config = `{name}_pitcher.json`. Same player can have both. Pitchers list persists in `pitchers.json`; image paths synced from roster on each download.

**Playing overlay / `SetPlayingSource`**: Accepts `PlayerViewModel`, `PitcherViewModel`, or `SoundButtonViewModel`. Sets `IsAnySourcePlaying`, populates overlay fields, sets `IsPlaying=true` on the source. `AudioService.PlaybackStopped` and `StopCurrentSoundCommand` both call `SetPlayingSource(null)`.

**Playlist resume after walkup**: `_spotifyContextIsPlaylist` tracks whether Spotify's last-played context was the playlist or a walkup. `TryCapturePlaylistPositionAsync` saves `{playlistUri, trackUri, positionMs}` before any walkup. `ToggleSpotifyAsync` uses these to `ResumeContextAsync` (exact position) or `PlayPlaylistAsync` (restart), never resumes the walkup track.

**Select Pitcher dialog click**: Uses `ListBox.SelectionChanged` event in code-behind (`SelectPitcherList_SelectionChanged`) — NOT a Button inside the DataTemplate. This gives full-row hit area. The handler calls `AddPitcherCommand` then clears `SelectedItem`.

**`GotKeyboardFocus` for time fields**: `TimeField_GotFocus` in code-behind calls `tb.Dispatcher.BeginInvoke(tb.SelectAll)` — the BeginInvoke is required because SelectAll before layout completes is a no-op.

**Scrolling**: `ScrollViewer.CanContentScroll="False"` on the Select Pitcher ListBox for pixel-based touchpad scrolling (physical scroll, not logical item-scroll).

**Player card equal height**: `UniformGrid` + inner `Border` with `VerticalAlignment="Top"` — cards render at content height, not stretched to cell height.

**Spotify PKCE auth**: Listens on `http://127.0.0.1:56669/` for callback. Tokens stored in `spotify_tokens.json` with `scopes` field; if stored scopes don't include all required scopes, tokens are invalidated and re-auth is triggered.

## SoundConfig record
```csharp
record SoundConfig(
    string? SpotifyUri = null,
    int StartMinutes = 0,
    int StartSeconds = 0,
    string? Mp3FileName = null,
    string? TrackName = null,
    string? ArtistName = null)
{
    int StartPositionMs => (StartMinutes * 60 + StartSeconds) * 1000;
}
```
Either `SpotifyUri` or `Mp3FileName` is set, not both.

## Color palette
- Background: `#111111`
- Card/panel: `#1C1C1C`
- Sidebar: `#0A0A0A`
- Accent/gold: `#C9A84C`
- Accent dark: `#6B5620`
- Text primary: `#FFFFFF`
- Text secondary: `#AAAAAA`
- Text muted: `#666666`
- Danger: `#8B0000`
- Pitcher panel bg: `#0D0D0D`
