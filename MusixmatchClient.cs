using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace lyrics_overlay;

public class SyncedLyricLine
{
    public string Text { get; set; } = "";
    public int StartTimeMs { get; set; }
    public string? Performer { get; set; }
}

public class MusixmatchClient
{
    private const string BaseUrl = "https://apic-appmobile.musixmatch.com/ws/1.1/";
    private readonly HttpClient _http = new();

    public string UserToken { get; private set; } = "";

    public MusixmatchClient()
    {
        AppLogger.Log("MusixmatchClient ctor");

        _http.DefaultRequestHeaders.TryAddWithoutValidation("Host", "apic-appmobile.musixmatch.com");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("authority", "apic-appmobile.musixmatch.com");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Cookie", "x-mxm-token-guid=");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-mxm-app-version", "10.1.1");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Agent", "Musixmatch/2025120901 CFNetwork/3860.300.31 Darwin/25.2.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Connection", "keep-alive");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    }

    public async Task EnsureTokenAsync()
    {
        AppLogger.Log($"Musixmatch EnsureTokenAsync begin, existingTokenLength={UserToken?.Length ?? 0}");

        if (!string.IsNullOrWhiteSpace(UserToken))
        {
            AppLogger.Log("Musixmatch token already present, skipping token fetch");
            return;
        }

        var url = $"{BaseUrl}token.get?app_id=mac-ios-v2.0";
        AppLogger.Log($"Musixmatch token URL: {url}");

        using var doc = await GetJsonAsync(url);

        var message = doc.RootElement.GetProperty("message");
        var header = message.GetProperty("header");
        int status = header.GetProperty("status_code").GetInt32();
        AppLogger.Log($"Musixmatch token status_code={status}");

        if (status != 200)
            throw new Exception($"Musixmatch token request failed: {status}");

        var body = message.GetProperty("body");
        UserToken = body.GetProperty("user_token").GetString() ?? "";

        AppLogger.Log($"Musixmatch token length={UserToken.Length}");

        if (string.IsNullOrWhiteSpace(UserToken))
            throw new Exception("Musixmatch returned empty user_token.");
    }

    public async Task<List<SyncedLyricLine>> GetSyncedLyricsAsync(
        string artist,
        string title,
        string album,
        string spotifyUri,
        int durationMs)
    {
        AppLogger.Log($"GetSyncedLyricsAsync begin | artist={artist} | title={title} | album={album} | spotifyUri={spotifyUri} | durationMs={durationMs}");

        await EnsureTokenAsync();

        using var macroDoc = await GetMacroSubtitlesAsync(artist, title, album, spotifyUri, durationMs);
        var root = macroDoc.RootElement;
        var macroCalls = root.GetProperty("message").GetProperty("body").GetProperty("macro_calls");

        var matcherTrackGet = macroCalls.GetProperty("matcher.track.get");
        int matcherStatus = matcherTrackGet.GetProperty("message").GetProperty("header").GetProperty("status_code").GetInt32();
        AppLogger.Log($"Musixmatch matcher.track.get status={matcherStatus}");

        if (matcherStatus != 200)
        {
            AppLogger.Log("matcher.track.get not successful, returning empty lyrics");
            return new List<SyncedLyricLine>();
        }

        var trackBody = matcherTrackGet.GetProperty("message").GetProperty("body");
        if (!trackBody.TryGetProperty("track", out var track))
        {
            AppLogger.Log("No track object in matcher.track.get body");
            return new List<SyncedLyricLine>();
        }

        bool instrumental = TryGetBool(track, "instrumental");
        bool hasRichSync = TryGetBool(track, "has_richsync");
        bool hasSubtitles = TryGetBool(track, "has_subtitles");

        string commonTrackId = track.TryGetProperty("commontrack_id", out var ctid) ? ctid.ToString() : "";
        string trackLength = track.TryGetProperty("track_length", out var tlen) ? tlen.ToString() : "";

        AppLogger.Log($"Track flags | instrumental={instrumental} | hasRichSync={hasRichSync} | hasSubtitles={hasSubtitles} | commontrack_id={commonTrackId} | track_length={trackLength}");

        if (instrumental)
        {
            AppLogger.Log("Track is instrumental");
            return new List<SyncedLyricLine>
            {
                new SyncedLyricLine { Text = "♪ Instrumental ♪", StartTimeMs = 0 }
            };
        }

        if (hasRichSync)
        {
            AppLogger.Log("Attempting richsync fetch");
            var rich = await GetKaraokeAsync(track);
            AppLogger.Log($"Richsync returned {rich.Count} lines");
            if (rich.Count > 0)
                return rich;
        }

        if (hasSubtitles)
        {
            AppLogger.Log("Attempting subtitle parse from macro");
            var synced = GetSyncedFromMacro(macroCalls);
            AppLogger.Log($"Subtitle parse returned {synced.Count} lines");
            if (synced.Count > 0)
                return synced;
        }

        AppLogger.Log("No richsync or subtitles found, returning empty list");
        return new List<SyncedLyricLine>();
    }

    private async Task<JsonDocument> GetMacroSubtitlesAsync(
        string artist,
        string title,
        string album,
        string spotifyUri,
        int durationMs)
    {
        double durSec = durationMs / 1000.0;

        var parameters = new Dictionary<string, string>
        {
            ["format"] = "json",
            ["namespace"] = "lyrics_richsynched",
            ["subtitle_format"] = "mxm",
            ["app_id"] = "mac-ios-v2.0",
            ["q_album"] = album ?? "",
            ["q_artist"] = artist ?? "",
            ["q_artists"] = artist ?? "",
            ["q_track"] = title ?? "",
            ["track_spotify_id"] = spotifyUri ?? "",
            ["q_duration"] = durSec.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["f_subtitle_length"] = Math.Floor(durSec).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["usertoken"] = UserToken,
            ["part"] = "track_lyrics_translation_status,track_structure,track_performer_tagging"
        };

        string url = $"{BaseUrl}macro.subtitles.get?{BuildQuery(parameters)}";
        AppLogger.Log($"Macro subtitles URL: {url}");

        return await GetJsonAsync(url);
    }

    private async Task<List<SyncedLyricLine>> GetKaraokeAsync(JsonElement track)
    {
        var result = new List<SyncedLyricLine>();

        if (!track.TryGetProperty("commontrack_id", out var commonTrackIdEl))
        {
            AppLogger.Log("GetKaraokeAsync missing commontrack_id");
            return result;
        }

        if (!track.TryGetProperty("track_length", out var trackLengthEl))
        {
            AppLogger.Log("GetKaraokeAsync missing track_length");
            return result;
        }

        var parameters = new Dictionary<string, string>
        {
            ["format"] = "json",
            ["subtitle_format"] = "mxm",
            ["app_id"] = "mac-ios-v2.0",
            ["f_subtitle_length"] = trackLengthEl.ToString(),
            ["q_duration"] = trackLengthEl.ToString(),
            ["commontrack_id"] = commonTrackIdEl.ToString(),
            ["usertoken"] = UserToken
        };

        string url = $"{BaseUrl}track.richsync.get?{BuildQuery(parameters)}";
        AppLogger.Log($"Richsync URL: {url}");

        using var doc = await GetJsonAsync(url);

        var message = doc.RootElement.GetProperty("message");
        int status = message.GetProperty("header").GetProperty("status_code").GetInt32();
        AppLogger.Log($"Richsync status={status}");

        if (status != 200)
            return result;

        var body = message.GetProperty("body");
        if (!body.TryGetProperty("richsync", out var richsync))
        {
            AppLogger.Log("Richsync body missing richsync object");
            return result;
        }

        if (!richsync.TryGetProperty("richsync_body", out var richsyncBodyEl))
        {
            AppLogger.Log("Richsync object missing richsync_body");
            return result;
        }

        string richsyncBody = richsyncBodyEl.GetString() ?? "";
        AppLogger.Log($"Richsync body length={richsyncBody.Length}");

        if (string.IsNullOrWhiteSpace(richsyncBody))
            return result;

        using var richDoc = JsonDocument.Parse(richsyncBody);

        foreach (var line in richDoc.RootElement.EnumerateArray())
        {
            double ts = line.GetProperty("ts").GetDouble();
            int startMs = (int)(ts * 1000);

            string text = "";
            if (line.TryGetProperty("l", out var words) && words.ValueKind == JsonValueKind.Array)
            {
                text = string.Concat(words.EnumerateArray().Select(w => w.GetProperty("c").GetString() ?? ""));
            }

            result.Add(new SyncedLyricLine
            {
                Text = string.IsNullOrWhiteSpace(text) ? "♪" : text,
                StartTimeMs = startMs
            });
        }

        AppLogger.Log($"Richsync parsed line count={result.Count}");
        return result;
    }

    private List<SyncedLyricLine> GetSyncedFromMacro(JsonElement macroCalls)
    {
        var result = new List<SyncedLyricLine>();
        AppLogger.Log("GetSyncedFromMacro begin");

        if (!macroCalls.TryGetProperty("track.subtitles.get", out var subtitlesGet))
        {
            AppLogger.Log("Macro missing track.subtitles.get");
            return result;
        }

        if (!subtitlesGet.TryGetProperty("message", out var message))
        {
            AppLogger.Log("track.subtitles.get missing message");
            return result;
        }

        if (!message.TryGetProperty("body", out var body))
        {
            AppLogger.Log("track.subtitles.get missing body");
            return result;
        }

        if (!body.TryGetProperty("subtitle_list", out var subtitleList))
        {
            AppLogger.Log("track.subtitles.get missing subtitle_list");
            return result;
        }

        if (subtitleList.ValueKind != JsonValueKind.Array || subtitleList.GetArrayLength() == 0)
        {
            AppLogger.Log("subtitle_list empty");
            return result;
        }

        var subtitle = subtitleList[0].GetProperty("subtitle");
        string subtitleBody = subtitle.GetProperty("subtitle_body").GetString() ?? "";
        AppLogger.Log($"Subtitle body length={subtitleBody.Length}");

        if (string.IsNullOrWhiteSpace(subtitleBody))
            return result;

        using var subtitleDoc = JsonDocument.Parse(subtitleBody);

        foreach (var line in subtitleDoc.RootElement.EnumerateArray())
        {
            string text = line.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "♪" : "♪";
            int startMs = 0;

            if (line.TryGetProperty("time", out var timeEl) &&
                timeEl.TryGetProperty("total", out var totalEl))
            {
                startMs = (int)(totalEl.GetDouble() * 1000);
            }

            result.Add(new SyncedLyricLine
            {
                Text = text,
                StartTimeMs = startMs
            });
        }

        AppLogger.Log($"Subtitle parsed line count={result.Count}");
        return result;
    }

    private static bool TryGetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetInt32() != 0,
            _ => false
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        AppLogger.Log($"HTTP GET begin: {url}");

        var response = await _http.GetAsync(url);
        AppLogger.Log($"HTTP GET status={(int)response.StatusCode} for {url}");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        AppLogger.Log($"HTTP GET response length={json.Length} for {url}");

        return JsonDocument.Parse(json);
    }

    private static string BuildQuery(Dictionary<string, string> parameters)
    {
        return string.Join("&", parameters.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value ?? "")}"));
    }
}