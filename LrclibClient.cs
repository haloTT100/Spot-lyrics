using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Core;

namespace lyrics_overlay;

public sealed class LrclibClient
{
    private const string BaseUrl = "https://lrclib.net";
    private const int MaxLyricsFileCharacters = 256 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Regex LrcTimestampRegex = new(
        @"\[(?<minutes>\d+):(?<seconds>\d{2})(?:\.(?<fraction>\d{1,3}))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "SpotLyrics/1.0 (https://github.com/haloTT100/Spot-lyrics)");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return http;
    }

    public async Task<LrclibLyricsResult?> GetLyricsAsync(
        string artist,
        string title,
        string album,
        int durationMs,
        Func<string, Task<(string original, string display)>> transformLyricTextAsync)
    {
        var parameters = new Dictionary<string, string>
        {
            ["track_name"] = title,
            ["artist_name"] = artist
        };

        if (!string.IsNullOrWhiteSpace(album))
            parameters["album_name"] = album;

        if (durationMs is >= 1000 and <= 3_600_000)
        {
            parameters["duration"] = (durationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        }

        string url = $"{BaseUrl}/api/get?{BuildQuery(parameters)}";
        AppLogger.Log($"LRCLIB lookup begin | artist={artist} | title={title} | album={album} | durationMs={durationMs}");

        try
        {
            using var response = await Http.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                AppLogger.Log("LRCLIB did not find a matching track");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                string retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) ?? "none";
                AppLogger.Log($"LRCLIB request failed with status={(int)response.StatusCode}, retryAfterSeconds={retryAfter}");
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    double retrySeconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1.0;
                    retrySeconds = Math.Clamp(retrySeconds, 1.0, 60.0);
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds));
                }
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseStream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var result = new LrclibLyricsResult
            {
                IsInstrumental = ReadBoolean(document.RootElement, "instrumental"),
                HasWordSync = ReadBoolean(document.RootElement, "hasWordSync")
            };

            if (document.RootElement.TryGetProperty("lyricsfile", out var lyricsFileElement) &&
                lyricsFileElement.ValueKind == JsonValueKind.String)
            {
                string lyricsFile = lyricsFileElement.GetString() ?? "";
                if (lyricsFile.Length > MaxLyricsFileCharacters)
                {
                    AppLogger.Log($"LRCLIB lyricsfile exceeded size limit ({lyricsFile.Length} characters)");
                }
                else if (!string.IsNullOrWhiteSpace(lyricsFile))
                {
                    await ReadLyricsFileAsync(lyricsFile, result, transformLyricTextAsync);
                }
            }

            if (document.RootElement.TryGetProperty("syncedLyrics", out var syncedLyricsElement) &&
                syncedLyricsElement.ValueKind == JsonValueKind.String)
            {
                string syncedLyrics = syncedLyricsElement.GetString() ?? "";
                if (result.SyncedLyrics.Count == 0)
                    result.SyncedLyrics = await ParseLrcAsync(syncedLyrics, transformLyricTextAsync);
            }

            AppLogger.Log($"LRCLIB result | wordSync={result.WordSyncedKaraokeLines.Count} lines | synced={result.SyncedLyrics.Count} lines | instrumental={result.IsInstrumental}");
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"LRCLIB lookup failed: {ex.Message}");
            return null;
        }
    }

    private static async Task ReadLyricsFileAsync(
        string yaml,
        LrclibLyricsResult result,
        Func<string, Task<(string original, string display)>> transformLyricTextAsync)
    {
        LyricsFileDocument? lyricsFile;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithDuplicateKeyChecking()
                .Build();
            lyricsFile = deserializer.Deserialize<LyricsFileDocument>(yaml);
        }
        catch (YamlException ex)
        {
            AppLogger.Log($"LRCLIB lyricsfile YAML parse failed: {ex.Message}");
            return;
        }

        if (lyricsFile == null || !string.Equals(lyricsFile.Version, "1.0", StringComparison.Ordinal))
        {
            AppLogger.Log("LRCLIB lyricsfile version is missing or unsupported");
            return;
        }

        result.IsInstrumental |= lyricsFile.Metadata?.Instrumental == true;

        if (lyricsFile.Lines == null)
            return;

        foreach (var sourceLine in lyricsFile.Lines
                     .Where(line => line != null && line.StartMs >= 0)
                     .OrderBy(line => line.StartMs))
        {
            int startMs = sourceLine.StartMs;
            if (sourceLine.EndMs.HasValue && sourceLine.EndMs.Value < startMs)
                continue;

            var sourceWords = (sourceLine.Words ?? new List<LyricsFileWord>())
                .Where(word => word != null && word.StartMs >= 0 && !string.IsNullOrEmpty(word.Text))
                .OrderBy(word => word.StartMs)
                .ToList();

            string sourceText = string.IsNullOrEmpty(sourceLine.Text)
                ? string.Concat(sourceWords.Select(word => word.Text))
                : sourceLine.Text;
            if (string.IsNullOrWhiteSpace(sourceText))
                continue;

            var transformedLine = await transformLyricTextAsync(sourceText);
            result.SyncedLyrics.Add(new SyncedLyricLine
            {
                Text = transformedLine.display,
                OriginalText = transformedLine.original,
                RomanizedText = transformedLine.display,
                StartTimeMs = startMs
            });

            if (sourceWords.Count == 0)
                continue;

            result.HasWordSync = true;
            var karaokeLine = new KaraokeLine
            {
                StartTimeMs = startMs,
                EndTimeMs = sourceLine.EndMs ?? startMs
            };

            for (int index = 0; index < sourceWords.Count; index++)
            {
                var sourceWord = sourceWords[index];
                if (sourceWord.EndMs.HasValue && sourceWord.EndMs.Value < sourceWord.StartMs)
                    continue;

                var transformedWord = await transformLyricTextAsync(sourceWord.Text);
                int nextStartMs = index + 1 < sourceWords.Count ? sourceWords[index + 1].StartMs : sourceLine.EndMs ?? sourceWord.StartMs;
                int endMs = sourceWord.EndMs ?? nextStartMs;

                karaokeLine.Words.Add(new KaraokeWord
                {
                    Word = transformedWord.display,
                    OriginalWord = transformedWord.original,
                    RomanizedWord = transformedWord.display,
                    OffsetMs = sourceWord.StartMs - startMs,
                    DurationMs = Math.Max(0, endMs - sourceWord.StartMs)
                });
            }

            if (karaokeLine.Words.Count == 0)
            {
                karaokeLine.Words.Add(new KaraokeWord
                {
                    Word = transformedLine.display,
                    OriginalWord = transformedLine.original,
                    RomanizedWord = transformedLine.display,
                    OffsetMs = 0,
                    DurationMs = Math.Max(0, (sourceLine.EndMs ?? startMs) - startMs)
                });
            }

            result.WordSyncedKaraokeLines.Add(karaokeLine);
            result.WordSyncedLyricLines.Add(new SyncedLyricLine
            {
                Text = transformedLine.display,
                OriginalText = transformedLine.original,
                RomanizedText = transformedLine.display,
                StartTimeMs = startMs
            });
        }
    }

    private static async Task<List<SyncedLyricLine>> ParseLrcAsync(
        string lyrics,
        Func<string, Task<(string original, string display)>> transformLyricTextAsync)
    {
        var result = new List<SyncedLyricLine>();
        foreach (string rawLine in lyrics.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = LrcTimestampRegex.Matches(rawLine);
            if (matches.Count == 0)
                continue;

            string text = rawLine[(matches[^1].Index + matches[^1].Length)..].TrimStart();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var transformed = await transformLyricTextAsync(text);
            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
                    !int.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                    continue;

                string fraction = match.Groups["fraction"].Value.PadRight(3, '0');
                int milliseconds = fraction.Length == 0
                    ? 0
                    : int.Parse(fraction[..3], CultureInfo.InvariantCulture);
                int timestampMs = (minutes * 60_000) + (seconds * 1_000) + milliseconds;

                result.Add(new SyncedLyricLine
                {
                    Text = transformed.display,
                    OriginalText = transformed.original,
                    RomanizedText = transformed.display,
                    StartTimeMs = timestampMs
                });
            }
        }

        return result.OrderBy(line => line.StartTimeMs).ToList();
    }

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static string BuildQuery(Dictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private sealed class LyricsFileDocument
    {
        [YamlMember(Alias = "version")]
        public string? Version { get; set; }

        [YamlMember(Alias = "metadata")]
        public LyricsFileMetadata? Metadata { get; set; }

        [YamlMember(Alias = "lines")]
        public List<LyricsFileLine>? Lines { get; set; }

        [YamlMember(Alias = "plain")]
        public string? Plain { get; set; }
    }

    private sealed class LyricsFileMetadata
    {
        [YamlMember(Alias = "title")]
        public string? Title { get; set; }

        [YamlMember(Alias = "artist")]
        public string? Artist { get; set; }

        [YamlMember(Alias = "album")]
        public string? Album { get; set; }

        [YamlMember(Alias = "duration_ms")]
        public int? DurationMs { get; set; }

        [YamlMember(Alias = "offset_ms")]
        public int? OffsetMs { get; set; }

        [YamlMember(Alias = "language")]
        public string? Language { get; set; }

        [YamlMember(Alias = "instrumental")]
        public bool? Instrumental { get; set; }
    }

    private sealed class LyricsFileLine
    {
        [YamlMember(Alias = "text")]
        public string? Text { get; set; }

        [YamlMember(Alias = "start_ms")]
        public int StartMs { get; set; }

        [YamlMember(Alias = "end_ms")]
        public int? EndMs { get; set; }

        [YamlMember(Alias = "words")]
        public List<LyricsFileWord>? Words { get; set; }
    }

    private sealed class LyricsFileWord
    {
        [YamlMember(Alias = "text")]
        public string Text { get; set; } = "";

        [YamlMember(Alias = "start_ms")]
        public int StartMs { get; set; }

        [YamlMember(Alias = "end_ms")]
        public int? EndMs { get; set; }
    }
}

public sealed class LrclibLyricsResult
{
    public bool IsInstrumental { get; set; }
    public bool HasWordSync { get; set; }
    public List<KaraokeLine> WordSyncedKaraokeLines { get; set; } = new();
    public List<SyncedLyricLine> WordSyncedLyricLines { get; set; } = new();
    public List<SyncedLyricLine> SyncedLyrics { get; set; } = new();
}
