namespace Bats_Sounds.Models;

// Unified sound config for both player walkup tracks and sidebar sound buttons.
// Either SpotifyUri or Mp3FileName is populated, not both.
public record SoundConfig(
    string? SpotifyUri = null,
    int StartMinutes = 0,
    int StartSeconds = 0,
    string? Mp3FileName = null,
    string? TrackName = null,
    string? ArtistName = null)
{
    public int StartPositionMs => (StartMinutes * 60 + StartSeconds) * 1000;
}
