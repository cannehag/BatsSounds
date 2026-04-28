using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bats_Sounds.Services;

public class SpotifyWebService
{
    private const string ClientId   = "87e9162b57e44cf881217f9df323822c";
    private const string RedirectUri = "http://127.0.0.1:56669/api/auth/callback";
    private const string Scopes     = "user-modify-playback-state user-read-playback-state playlist-read-private playlist-read-collaborative";

    public record PlaybackInfo(
        bool IsPlaying,
        string? PlaylistUri,
        string? PlaylistName,
        string? TrackName,
        string? ArtistName,
        string? TrackUri = null,
        int ProgressMs = 0);

    private readonly HttpClient _http = new();
    private readonly string _tokenFile;
    private readonly Dictionary<string, string> _playlistNameCache = new();

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    // Has stored credentials (even if access token is expired — refresh will fix it)
    public bool IsAuthenticated => !string.IsNullOrEmpty(_refreshToken);

    public SpotifyWebService(string appDir)
    {
        _tokenFile = Path.Combine(appDir, "spotify_tokens.json");
        LoadTokens();
        InvalidateTokenIfScopesMissing();
    }

    // If the stored token pre-dates a scope change, force re-auth
    private void InvalidateTokenIfScopesMissing()
    {
        if (!File.Exists(_tokenFile)) return;
        try
        {
            var json = File.ReadAllText(_tokenFile);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("scopes", out var s)) { ClearTokens(); return; }
            var stored = s.GetString() ?? "";
            foreach (var required in Scopes.Split(' '))
                if (!stored.Contains(required)) { ClearTokens(); return; }
        }
        catch { ClearTokens(); }
    }

    private void ClearTokens()
    {
        _accessToken  = null;
        _refreshToken = null;
        _tokenExpiry  = DateTime.MinValue;
        try { if (File.Exists(_tokenFile)) File.Delete(_tokenFile); } catch { }
    }

    // ── Auth ────────────────────────────────────────────────────────────────

    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        var verifier  = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state     = Base64UrlEncode(RandomNumberGenerator.GetBytes(12));

        var authUrl =
            "https://accounts.spotify.com/authorize" +
            $"?client_id={ClientId}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&code_challenge_method=S256" +
            $"&code_challenge={challenge}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            $"&state={state}";

        // Start listener before opening browser so we don't miss the redirect
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        var codeTask = ListenForCallbackAsync(cts.Token);

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var code = await codeTask;
        await ExchangeCodeAsync(code, verifier, cts.Token);
    }

    private static async Task<string> ListenForCallbackAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:56669/");
        listener.Start();

        ct.Register(() => listener.Stop());

        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync();
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Spotify auth timed out.", ct);
        }

        var query = ParseQuery(ctx.Request.Url?.Query ?? "");

        if (!query.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            throw new Exception(query.GetValueOrDefault("error", "No code returned from Spotify."));

        var html = Encoding.UTF8.GetBytes(
            "<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
            "<h2>&#10003; Connected to Bats Sounds!</h2><p>You can close this tab.</p></body></html>");
        ctx.Response.ContentLength64 = html.Length;
        ctx.Response.ContentType = "text/html";
        await ctx.Response.OutputStream.WriteAsync(html, ct);
        ctx.Response.Close();

        return code;
    }

    private async Task ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var resp = await _http.PostAsync(
            "https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["code"]          = code,
                ["redirect_uri"]  = RedirectUri,
                ["client_id"]     = ClientId,
                ["code_verifier"] = verifier,
            }), ct);

        resp.EnsureSuccessStatusCode();
        StoreTokenResponse(await resp.Content.ReadAsStringAsync(ct));
    }

    // ── Token management ────────────────────────────────────────────────────

    private async Task EnsureTokenAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _tokenExpiry.AddMinutes(-1)) return;
        if (_refreshToken == null) throw new InvalidOperationException("Not authenticated.");
        await RefreshAsync(ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var resp = await _http.PostAsync(
            "https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = _refreshToken!,
                ["client_id"]     = ClientId,
            }), ct);

        resp.EnsureSuccessStatusCode();
        StoreTokenResponse(await resp.Content.ReadAsStringAsync(ct));
    }

    private void StoreTokenResponse(string json)
    {
        var doc = JsonDocument.Parse(json);
        _accessToken  = doc.RootElement.GetProperty("access_token").GetString();
        _tokenExpiry  = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetDouble());
        if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
            _refreshToken = rt.GetString();
        SaveTokens();
    }

    private void SaveTokens()
    {
        File.WriteAllText(_tokenFile, JsonSerializer.Serialize(new
        {
            access_token  = _accessToken,
            refresh_token = _refreshToken,
            expiry        = _tokenExpiry,
            scopes        = Scopes,
        }));
    }

    private void LoadTokens()
    {
        if (!File.Exists(_tokenFile)) return;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(_tokenFile));
            _accessToken  = doc.RootElement.GetProperty("access_token").GetString();
            _refreshToken = doc.RootElement.GetProperty("refresh_token").GetString();
            _tokenExpiry  = doc.RootElement.GetProperty("expiry").GetDateTime();
        }
        catch { }
    }

    // ── Playback API ─────────────────────────────────────────────────────────

    public async Task<bool?> GetIsPlayingAsync(CancellationToken ct = default)
    {
        var info = await GetCurrentPlaybackAsync(ct);
        return info?.IsPlaying;
    }

    public async Task<PlaybackInfo?> GetCurrentPlaybackAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var resp = await ApiGetAsync("me/player", ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) return null;

        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;

        var isPlaying  = root.TryGetProperty("is_playing",  out var ip) && ip.GetBoolean();
        var progressMs = root.TryGetProperty("progress_ms", out var pm) ? pm.GetInt32() : 0;

        string? trackName = null, artistName = null, trackUri = null;
        if (root.TryGetProperty("item", out var item) && item.ValueKind != JsonValueKind.Null)
        {
            trackName  = item.TryGetProperty("name", out var tn) ? tn.GetString() : null;
            trackUri   = item.TryGetProperty("uri",  out var tu) ? tu.GetString() : null;
            if (item.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
                artistName = artists[0].TryGetProperty("name", out var an) ? an.GetString() : null;
        }

        string? playlistUri = null, playlistName = null;
        if (root.TryGetProperty("context", out var ctx) && ctx.ValueKind != JsonValueKind.Null)
        {
            playlistUri = ctx.TryGetProperty("uri", out var uri) ? uri.GetString() : null;
            if (playlistUri?.StartsWith("spotify:playlist:") == true)
            {
                var id = playlistUri.Split(':')[2];
                playlistName = await GetPlaylistNameByIdAsync(id, ct);
            }
        }

        return new PlaybackInfo(isPlaying, playlistUri, playlistName, trackName, artistName, trackUri, progressMs);
    }

    public async Task<(string? TrackName, string? ArtistName)> GetTrackInfoAsync(string trackUri, CancellationToken ct = default)
    {
        var parts = trackUri.Split(':');
        if (parts.Length < 3 || parts[1] != "track") return (null, null);

        await EnsureTokenAsync(ct);
        var resp = await ApiGetAsync($"tracks/{parts[2]}", ct);
        if (!resp.IsSuccessStatusCode) return (null, null);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var trackName = doc.RootElement.TryGetProperty("name", out var tn) ? tn.GetString() : null;
        string? artistName = null;
        if (doc.RootElement.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
            artistName = artists[0].TryGetProperty("name", out var an) ? an.GetString() : null;

        return (trackName, artistName);
    }

    public async Task<string?> GetPlaylistNameAsync(string playlistUri, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var uri = ToSpotifyUri(playlistUri);
        if (!uri.StartsWith("spotify:playlist:")) return null;
        var id = uri.Split(':')[2];
        return await GetPlaylistNameByIdAsync(id, ct);
    }

    private async Task<string?> GetPlaylistNameByIdAsync(string playlistId, CancellationToken ct)
    {
        if (_playlistNameCache.TryGetValue(playlistId, out var cached)) return cached;
        var resp = await ApiGetAsync($"playlists/{playlistId}?fields=name", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (name != null) _playlistNameCache[playlistId] = name;
        return name;
    }

    public async Task PlayPlaylistAsync(string playlistInput, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var uri  = ToSpotifyUri(playlistInput);
        var body = JsonSerializer.Serialize(new { context_uri = uri });

        var resp = await ApiPutRawAsync("me/player/play", body, ct);
        if (resp.IsSuccessStatusCode) return;

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // No active device — find one and retry with explicit device_id
            var deviceId = await GetFirstAvailableDeviceAsync(ct)
                ?? throw new Exception("No Spotify device found. Make sure Spotify is open on a device.");
            resp = await ApiPutRawAsync($"me/player/play?device_id={deviceId}", body, ct);
            if (resp.IsSuccessStatusCode) return;
        }

        throw new Exception(await ReadSpotifyErrorAsync(resp, ct));
    }

    public async Task PlayTrackAsync(string trackUri, int positionMs, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var body = JsonSerializer.Serialize(new { uris = new[] { trackUri }, position_ms = positionMs });

        var resp = await ApiPutRawAsync("me/player/play", body, ct);
        if (resp.IsSuccessStatusCode) return;

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            var deviceId = await GetFirstAvailableDeviceAsync(ct)
                ?? throw new Exception("No Spotify device found. Make sure Spotify is open on a device.");
            resp = await ApiPutRawAsync($"me/player/play?device_id={deviceId}", body, ct);
            if (resp.IsSuccessStatusCode) return;
        }

        throw new Exception(await ReadSpotifyErrorAsync(resp, ct));
    }

    public async Task ResumeContextAsync(string contextUri, string trackUri, int positionMs, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var body = JsonSerializer.Serialize(new
        {
            context_uri = contextUri,
            offset       = new { uri = trackUri },
            position_ms  = positionMs,
        });

        var resp = await ApiPutRawAsync("me/player/play", body, ct);
        if (resp.IsSuccessStatusCode) return;

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            var deviceId = await GetFirstAvailableDeviceAsync(ct)
                ?? throw new Exception("No Spotify device found. Make sure Spotify is open on a device.");
            resp = await ApiPutRawAsync($"me/player/play?device_id={deviceId}", body, ct);
            if (resp.IsSuccessStatusCode) return;
        }

        throw new Exception(await ReadSpotifyErrorAsync(resp, ct));
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var resp = await ApiPutRawAsync("me/player/pause", null, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
            throw new Exception(await ReadSpotifyErrorAsync(resp, ct));
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var resp = await ApiPutRawAsync("me/player/play", null, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
            throw new Exception(await ReadSpotifyErrorAsync(resp, ct));
    }

    private async Task<string?> GetFirstAvailableDeviceAsync(CancellationToken ct)
    {
        var resp = await ApiGetAsync("me/player/devices", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("devices", out var devices)) return null;
        foreach (var d in devices.EnumerateArray())
            return d.GetProperty("id").GetString();
        return null;
    }

    // ── HTTP helpers ─────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> ApiGetAsync(string path, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return await _http.SendAsync(req, ct);
    }

    private async Task<HttpResponseMessage> ApiPutRawAsync(string path, string? jsonBody, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"https://api.spotify.com/v1/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (jsonBody != null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await _http.SendAsync(req, ct);
    }

    private static async Task<string> ReadSpotifyErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString() ?? resp.ReasonPhrase ?? "Unknown error";
        }
        catch { }
        return $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ToSpotifyUri(string input)
    {
        input = input.Trim();
        if (input.StartsWith("spotify:")) return input;
        // https://open.spotify.com/playlist/ID?...
        if (input.Contains("open.spotify.com/"))
        {
            var path = new Uri(input).AbsolutePath.TrimStart('/'); // e.g. playlist/37i9...
            return "spotify:" + path.Replace('/', ':');
        }
        return $"spotify:playlist:{input}";
    }

    private static string GenerateCodeVerifier()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(64));

    private static string GenerateCodeChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(
                p => Uri.UnescapeDataString(p[0]),
                p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
}
