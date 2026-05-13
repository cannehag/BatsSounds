using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Bats_Sounds.Services;

public record ReleaseInfo(string TagName, string Version, string DownloadUrl);

public class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/cannehag/BatsSounds/releases/latest";

    private static readonly HttpClient _http = new();

    static UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BatsSounds-Updater");
    }

    public static string CurrentVersion
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<ReleaseInfo?> CheckForUpdateAsync()
    {
        var json = await _http.GetStringAsync(ApiUrl);
        var root = JsonDocument.Parse(json).RootElement;
        var tag  = root.GetProperty("tag_name").GetString() ?? "";
        var latest = tag.TrimStart('v');

        if (!Version.TryParse(latest, out var lv)) return null;
        if (!Version.TryParse(CurrentVersion, out var cv)) return null;
        if (lv <= cv) return null;

        string? url = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if ((asset.GetProperty("name").GetString() ?? "").EndsWith(".zip"))
            {
                url = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        return url == null ? null : new ReleaseInfo(tag, latest, url);
    }

    public async Task DownloadAndInstallAsync(ReleaseInfo release)
    {
        var tempDir = Path.GetTempPath();
        var tempZip = Path.Combine(tempDir, "BatsSounds_update.zip");
        var tempExe = Path.Combine(tempDir, "BatsSounds_update.exe");
        var current = Environment.ProcessPath!;

        var bytes = await _http.GetByteArrayAsync(release.DownloadUrl);
        await File.WriteAllBytesAsync(tempZip, bytes);

        using (var zip = ZipFile.OpenRead(tempZip))
        {
            var entry = zip.Entries.First(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            entry.ExtractToFile(tempExe, overwrite: true);
        }
        File.Delete(tempZip);

        var script = Path.Combine(tempDir, "BatsSounds_update.ps1");
        File.WriteAllText(script,
            $"Start-Sleep -Seconds 2\r\n" +
            $"Copy-Item -Force '{tempExe}' '{current}'\r\n" +
            $"Start-Process '{current}'\r\n" +
            $"Remove-Item '{tempExe}' -ErrorAction SilentlyContinue\r\n" +
            $"Remove-Item $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue\r\n");

        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell",
            Arguments       = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
            CreateNoWindow  = true,
            UseShellExecute = false,
        });

        Application.Current.Shutdown();
    }
}
