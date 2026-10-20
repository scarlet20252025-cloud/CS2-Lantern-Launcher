using System.Text.RegularExpressions;

namespace Lantern.Core;

public static class Subscriptions
{
    public static string CachePath(string steamRoot, string activeUser) => Path.Combine(steamRoot, "userdata", activeUser, "ugc", "730_subscriptions.vdf");
    public static IReadOnlyList<string> Read(string file)
    {
        if (!File.Exists(file)) return Array.Empty<string>();
        return (Kv.Parse(File.ReadAllText(file)).Node("subscribedfiles")?.Children ?? new())
            .Where(n => n.Get("disabled_locally") != "1").Select(n => n.Get("publishedfileid"))
            .Where(id => Regex.IsMatch(id, @"^[1-9]\d{4,19}$") && ulong.TryParse(id, out _)).Distinct().ToArray();
    }
    public static string DownloadResult(string steamRoot, string id)
    {
        var file = Path.Combine(steamRoot, "logs", "workshop_log.txt");
        if (!File.Exists(file)) return "";
        // Steam can keep this file open. Read at most its last 256 KiB.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Max(0, stream.Length - 262144), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var line = reader.ReadToEnd().Split('\n').LastOrDefault(l => l.Contains("[AppID 730]") && l.Contains("Download item " + id + " result :"));
        if (line == null) return "";
        if (line.Contains("Not enough free disk space")) return "上次下载失败：磁盘空间不足";
        if (line.Contains("No Error")) return "有下载成功记录，未找到完整可用地图";
        return "上次下载结果：" + line[(line.IndexOf("result :", StringComparison.Ordinal) + 8)..].Trim();
    }
}
