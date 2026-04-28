using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bats_Sounds.Services;

public class SpotifyService
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public bool IsSpotifyRunning()
    {
        return Process.GetProcessesByName("Spotify").Length > 0;
    }

    public bool IsSpotifyPlaying()
    {
        var procs = Process.GetProcessesByName("Spotify");
        return procs.Any(p => p.MainWindowTitle.Contains(" - "));
    }

    public void Pause()
    {
        if (IsSpotifyPlaying())
            TogglePlayPause();
    }

    public void TogglePlayPause()
    {
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, 0);
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_KEYUP, 0);
    }
}
