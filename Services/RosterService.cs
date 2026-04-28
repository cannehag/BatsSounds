using System.IO;
using System.Net.Http;
using Bats_Sounds.Models;
using HtmlAgilityPack;

namespace Bats_Sounds.Services;

public class RosterService
{
    private readonly HttpClient _http;
    private readonly string _imagesDir;

    public RosterService(string imagesDir)
    {
        _imagesDir = imagesDir;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<(List<Player> Players, string? Error)> FetchRosterAsync(string url)
    {
        try
        {
            var html = await _http.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var players = ParsePlayers(doc, url);

            if (players.Count == 0)
                return (players, "No players found — the page may require JavaScript. Check the URL and try again.");

            return (players, null);
        }
        catch (Exception ex)
        {
            return (new List<Player>(), $"Failed to load roster: {ex.Message}");
        }
    }

    private List<Player> ParsePlayers(HtmlDocument doc, string baseUrl)
    {
        var players = new List<Player>();
        var baseUri = new Uri(baseUrl);

        // Primary: stats.baseboll-softboll.se table format with td.player and YOB column
        var rows = doc.DocumentNode.SelectNodes("//table//tbody//tr");
        if (rows != null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var playerCell = row.SelectSingleNode(".//td[@class='player']//a")
                              ?? row.SelectSingleNode(".//td[contains(@class,'player')]//a");
                if (playerCell == null) continue;

                var name = HtmlEntity.DeEntitize(playerCell.InnerText).Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Length < 3) continue;
                if (!seen.Add(name)) continue;

                var href = playerCell.GetAttributeValue("href", "");
                string? profileUrl = string.IsNullOrEmpty(href) ? null
                    : href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href
                    : new Uri(baseUri, href).ToString();

                // YOB is the 5th <td> (index 4)
                int? yob = null;
                var cells = row.SelectNodes(".//td");
                if (cells != null && cells.Count >= 5)
                {
                    var yobText = cells[4].InnerText.Trim();
                    if (int.TryParse(yobText, out var y) && y > 1900 && y < 2030)
                        yob = y;
                }

                players.Add(new Player { Name = name, ProfileUrl = profileUrl, BirthYear = yob });
            }

            if (players.Count > 0)
                return players;
        }

        // Fallback: any link with /players/ in the href
        var playerLinks = doc.DocumentNode
            .SelectNodes("//a[contains(@href, '/players/')]")?
            .Where(n => !string.IsNullOrWhiteSpace(n.InnerText))
            .ToList();

        if (playerLinks != null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in playerLinks)
            {
                var name = HtmlEntity.DeEntitize(link.InnerText).Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Length < 3) continue;
                if (!seen.Add(name)) continue;

                var href = link.GetAttributeValue("href", "");
                string? profileUrl = string.IsNullOrEmpty(href) ? null
                    : href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href
                    : new Uri(baseUri, href).ToString();

                players.Add(new Player { Name = name, ProfileUrl = profileUrl });
            }
        }

        return players;
    }

    public async Task DownloadPlayerImagesAsync(
        List<Player> players,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_imagesDir);

        var tasks = players.Select(async player =>
        {
            if (player.ProfileUrl == null)
                return;

            var imagePath = Path.Combine(_imagesDir, PlayerFileName(player) + ".jpg");
            if (File.Exists(imagePath))
            {
                player.ImagePath = imagePath;
                progress?.Report(player.Name);
                return;
            }

            try
            {
                var imageUrl = await FindPlayerImageUrlAsync(player.ProfileUrl, cancellationToken);
                if (imageUrl == null) return;

                var bytes = await _http.GetByteArrayAsync(imageUrl, cancellationToken);
                await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken);
                player.ImagePath = imagePath;
                progress?.Report(player.Name);
            }
            catch
            {
                // Image download failure is non-fatal
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task<string?> FindPlayerImageUrlAsync(string profileUrl, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(profileUrl, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var candidates = doc.DocumentNode.SelectNodes(
                "//img[contains(@class,'player') or contains(@class,'profile') or contains(@class,'photo') or contains(@class,'avatar') or contains(@id,'photo') or contains(@id,'player')]");

            if (candidates == null || candidates.Count == 0)
            {
                var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                if (ogImage != null)
                    return ogImage.GetAttributeValue("content", null);

                candidates = doc.DocumentNode.SelectNodes("//img[@src and string-length(@src) > 10]");
            }

            if (candidates == null) return null;

            var src = candidates
                .Select(n => n.GetAttributeValue("src", "")
                           .Trim()
                           .Replace("&amp;", "&"))
                .FirstOrDefault(s => !string.IsNullOrEmpty(s)
                                  && !s.Contains("logo") && !s.Contains("icon") && !s.Contains("flag"));

            if (src == null) return null;

            if (src.StartsWith("//"))       src = "https:" + src;
            else if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                src = new Uri(new Uri(profileUrl), src).ToString();

            return src;
        }
        catch
        {
            return null;
        }
    }

    // "Cannehag Casper_2007"  or  "Cannehag Casper" if no YOB
    public static string PlayerFileName(Player player)
        => player.BirthYear.HasValue
            ? $"{SanitizeFileName(player.Name)}_{player.BirthYear}"
            : SanitizeFileName(player.Name);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
