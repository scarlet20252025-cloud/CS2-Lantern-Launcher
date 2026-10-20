using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lantern.Core;

public sealed record WorkshopItem(string Id, string Title, string PreviewUrl, string Tags, bool IsMap, long Bytes = 0);

public sealed class Workshop
{
    public static IReadOnlyList<string> ParseIds(string text)
    {
        var links = Regex.Matches(text, @"https?://steamcommunity\.com/(?:sharedfiles|workshop)/filedetails/\?id=[1-9]\d{4,19}", RegexOptions.IgnoreCase);
        return links.Count > 0 ? links.Select(m => ParseId(m.Value)).Distinct().ToArray() : new[] { ParseId(text) };
    }
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    public static string ParseId(string input)
    {
        input = input.Trim();
        if (Regex.IsMatch(input, @"^[1-9]\d{4,19}$") && ulong.TryParse(input, out _)) return input;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath is "/sharedfiles/filedetails/" or "/sharedfiles/filedetails" or "/workshop/filedetails/" or "/workshop/filedetails")
        {
            var m = Regex.Match(uri.Query, @"(?:^\?|&)id=([1-9]\d{4,19})(?:&|$)");
            if (m.Success && ulong.TryParse(m.Groups[1].Value, out _)) return m.Groups[1].Value;
        }
        throw new ArgumentException("请粘贴 steamcommunity.com 工坊条目链接或数字 ID。");
    }

    public async Task<List<WorkshopItem>> Fetch(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var result = new List<WorkshopItem>();
        foreach (var batch in ids.Distinct().Chunk(50))
        {
            var fields = new List<KeyValuePair<string, string>> { new("itemcount", batch.Length.ToString()) };
            for (var i = 0; i < batch.Length; i++) fields.Add(new($"publishedfileids[{i}]", ParseId(batch[i])));
            using var response = await Client.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", new FormUrlEncodedContent(fields), ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in json.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
            {
                if (item.GetProperty("result").GetInt32() != 1) continue;
                if (!item.TryGetProperty("consumer_app_id", out var app) || app.GetInt32() != 730) continue;
                var tags = item.TryGetProperty("tags", out var t) ? t.EnumerateArray().Select(x => x.GetProperty("tag").GetString() ?? "").ToArray() : Array.Empty<string>();
                bool collection = item.TryGetProperty("file_type", out var ft) && ft.GetInt32() == 2;
                result.Add(new(item.GetProperty("publishedfileid").GetString()!, item.GetProperty("title").GetString() ?? "未命名地图",
                    item.TryGetProperty("preview_url", out var p) ? p.GetString() ?? "" : "", string.Join(" · ", tags), !collection && tags.Any(s => s.Equals("Map", StringComparison.OrdinalIgnoreCase)),
                    item.TryGetProperty("file_size", out var size) && long.TryParse(size.ToString(), out var bytes) ? bytes : 0));
            }
        }
        return result;
    }

    public async Task<string> CachePreview(WorkshopItem item, string cache, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, ParseId(item.Id) + ".jpg");
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7)) return path;
        if (!Uri.TryCreate(item.PreviewUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") return "";
        // Only Steam's image CDNs; metadata must never become an arbitrary local/network fetch.
        if (!new[] { "steamstatic.com", "steamusercontent.com", "akamaihd.net" }.Any(d => uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))) return "";
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 5 * 1024 * 1024) return "";
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[32768]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > 5 * 1024 * 1024) return ""; output.Write(buffer, 0, count); }
        await File.WriteAllBytesAsync(path + ".tmp", output.ToArray(), ct); File.Move(path + ".tmp", path, true);
        return path;
    }
}
