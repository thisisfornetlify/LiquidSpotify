using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiquidSpotify;

internal sealed record LyricLine(double Time, string Text);
internal sealed record LyricResult(IReadOnlyList<LyricLine> Lines, bool Synced, string Status);

internal static partial class LyricsService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly Dictionary<string, LyricResult> Cache = new();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(18), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiquidSpotify/1.4 (Windows desktop lyrics player)");
        return client;
    }
    internal static async Task<LyricResult> Find(string title, string artist, string album, double duration, CancellationToken token)
    {
        var key = $"{title}\n{artist}\n{album}\n{Math.Round(duration)}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        var url = "https://lrclib.net/api/get?" + query + "&album_name=" + Uri.EscapeDataString(album);
        if (duration > 0) url += "&duration=" + duration.ToString("0.###", CultureInfo.InvariantCulture);
        using var response = await Client.GetAsync(url, token);
        LyricResult? result = null;
        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            result = ReadRecord(doc.RootElement);
        }
        else if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
        if (result == null || result.Lines.Count == 0 && !result.Status.StartsWith("Instrumental", StringComparison.Ordinal))
        {
            using var search = await Client.GetAsync("https://lrclib.net/api/search?" + query, token);
            search.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await search.Content.ReadAsStringAsync(token));
            var candidates = doc.RootElement.EnumerateArray()
                .Where(r => Normalize(Text(r, "trackName")) == Normalize(title)
                    && Normalize(Text(r, "artistName")) == Normalize(artist))
                .Where(r => duration <= 0 || !r.TryGetProperty("duration", out var d) || Math.Abs(d.GetDouble() - duration) <= 3)
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(Text(r, "syncedLyrics")))
                .ThenBy(r => r.TryGetProperty("duration", out var d) ? Math.Abs(d.GetDouble() - duration) : 1000)
                .ToList();
            if (candidates.Count > 0) result = ReadRecord(candidates[0]);
        }
        result ??= new LyricResult([], false, "No lyrics found for this recording. Try loading an LRC file.");
        token.ThrowIfCancellationRequested();
        if (Cache.Count >= 24) Cache.Clear();
        Cache[key] = result;
        return result;
    }
    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    internal static void ClearCache() => Cache.Clear();
    private static string Text(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static LyricResult ReadRecord(JsonElement record)
    {
        if (record.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True)
            return new LyricResult([], false, "Instrumental");
        var synced = Text(record, "syncedLyrics");
        var lines = ParseLrc(synced);
        if (lines.Count > 0) return new LyricResult(lines, true, "Synced lyrics · LRCLIB");
        var plain = Text(record, "plainLyrics");
        return new LyricResult(PlainLines(plain), false, string.IsNullOrWhiteSpace(plain) ? "Lyrics unavailable for this recording." : "Lyrics · LRCLIB · timing unavailable");
    }
    internal static IReadOnlyList<LyricLine> PlainLines(string text) => text.Replace("\r", "").Split('\n')
        .Where(s => !string.IsNullOrWhiteSpace(s)).Take(600).Select(s => new LyricLine(-1, s.Trim())).ToList();
    internal static IReadOnlyList<LyricLine> ParseLrc(string text)
    {
        var lines = new List<LyricLine>();
        var offsetMatch = Regex.Match(text, @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        var offset = offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var ms) ? ms / 1000 : 0;
        foreach (var row in text.Replace("\r", "").Split('\n'))
        {
            var stamps = Regex.Matches(row, @"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]");
            if (stamps.Count == 0) continue;
            var words = Regex.Replace(row, @"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", "").Trim();
            words = Regex.Replace(words, @"<\d{1,3}:\d{2}(?:\.\d{1,3})?>", "");
            foreach (Match stamp in stamps)
            {
                var minutes = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                if (seconds >= 60) continue;
                var fraction = stamp.Groups[3].Success ? double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                lines.Add(new LyricLine(Math.Max(0, minutes * 60 + seconds + fraction + offset), string.IsNullOrWhiteSpace(words) ? "♪" : words));
            }
        }
        return lines.OrderBy(l => l.Time).Take(600).ToList();
    }
}

