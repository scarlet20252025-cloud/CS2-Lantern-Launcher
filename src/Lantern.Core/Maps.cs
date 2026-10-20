using System.Text;
using System.Text.RegularExpressions;

namespace Lantern.Core;

// WPF Selector hashes items. Mutable record value hashes break selection after metadata updates.
// Keep reference identity stable; explicit Identity is used for rescans and deduplication.
public sealed class MapEntry
{
    public string Id { get; init; } = "";
    public string Title { get; set; } = "";
    public string MapName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public long Bytes { get; init; }
    public bool Official { get; init; }
    public bool ValidatedRemote { get; set; }
    public string PreviewPath { get; set; } = "";
    public string Tags { get; set; } = "";
    public string PendingReason { get; set; } = "";
    public string Availability => Official ? "游戏自带 · 可开图" : Local ? $"可开图 · {Bytes / 1048576d:F0} MB" : PendingReason.Length > 0 ? PendingReason : "待下载 · 请在 Steam 完成下载";
    public bool Local => FilePath.Length > 0;
    public string Identity => Official ? "official:" + MapName : Id + ":" + MapName;
    public string Subtitle => Official ? "游戏自带地图" : $"{Id}  ·  {(Local ? $"本地文件 · {Bytes / 1048576d:F0} MB" : "未下载 · 在 Steam 订阅")}";
    public string WorkshopUrl => "https://steamcommunity.com/sharedfiles/filedetails/?id=" + Id;
}

public sealed record Installation(string Root, IReadOnlyList<string> Libraries);
public sealed record ScanResult(List<MapEntry> Maps, List<string> Warnings);

public static class Discovery
{
    public static Installation Detect(string steamRoot, string? overrideRoot = null)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(steamRoot)) libraries.Add(Path.GetFullPath(steamRoot));
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (var n in Kv.Parse(File.ReadAllText(vdf)).Node("libraryfolders")?.Children ?? new())
            {
                var p = n.Get("path", n.Value ?? "");
                if (Directory.Exists(p)) libraries.Add(Path.GetFullPath(p));
            }
        string root = overrideRoot ?? "";
        foreach (var lib in libraries)
        {
            var manifest = Path.Combine(lib, "steamapps", "appmanifest_730.acf");
            if (!File.Exists(manifest)) continue;
            var dir = Kv.Parse(File.ReadAllText(manifest)).Node("AppState")?.Get("installdir");
            if (string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(dir)) root = Path.Combine(lib, "steamapps", "common", dir);
        }
        return new(root, libraries.ToArray());
    }

    public static IReadOnlyList<string> WorkshopRoots(Installation install, IEnumerable<string>? extraRoots = null)
    {
        var roots = install.Libraries.Select(l => Path.Combine(l, "steamapps", "workshop", "content", "730")).ToList();
        if (install.Root.Length > 0)
        {
            roots.Add(Path.Combine(install.Root, "game", "csgo", "maps", "workshop"));
            roots.Add(Path.Combine(install.Root, "game", "bin", "win64", "steamapps", "workshop", "content", "730"));
        }
        roots.AddRange(extraRoots ?? Array.Empty<string>());
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static ScanResult Scan(Installation install, IEnumerable<string>? extraRoots = null)
    {
        var result = new List<MapEntry>(); var warnings = new List<string>();
        var roots = WorkshopRoots(install, extraRoots);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 5 };
                foreach (var file in Directory.EnumerateFiles(root, "*.vpk", options))
                {
                    if (Regex.IsMatch(Path.GetFileNameWithoutExtension(file), @"_\d{3}$")) continue;
                    var segments = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
                    var id = segments.FirstOrDefault(s => Regex.IsMatch(s, @"^\d{5,20}$")) ?? "";
                    if (id.Length == 0 && Regex.IsMatch(new DirectoryInfo(root).Name, @"^\d{5,20}$")) id = new DirectoryInfo(root).Name;
                    if (id.Length == 0) { warnings.Add("缺少工坊 ID，跳过：" + file); continue; }
                    try
                    {
                        var names = Vpk.ReadMapNames(file);
                        if (names.Count == 0) continue; // skins, scripts and other non-map UGC
                        var title = "";
                        var publish = Path.Combine(Path.GetDirectoryName(file)!, "publish_data.txt");
                        if (File.Exists(publish)) title = Kv.Parse(File.ReadAllText(publish)).Node("publish_data")?.Get("title") ?? "";
                        long bytes = new FileInfo(file).Length;
                        if (file.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase))
                        {
                            var prefix = Path.GetFileName(file)[..^8];
                            foreach (var part in Directory.EnumerateFiles(Path.GetDirectoryName(file)!, prefix + "_*.vpk"))
                                if (Regex.IsMatch(Path.GetFileName(part), "^" + Regex.Escape(prefix) + @"_\d{3}\.vpk$")) bytes += new FileInfo(part).Length;
                        }
                        foreach (var name in names)
                            result.Add(new() { Id = id, Title = title.Length > 0 ? title : name, MapName = name, FilePath = file, Bytes = bytes });
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { warnings.Add(Path.GetFileName(file) + ": " + e.Message); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(root + ": " + e.Message); }
        }
        var mapsPath = Path.Combine(install.Root, "game", "csgo", "maps");
        if (Directory.Exists(mapsPath))
            foreach (var file in Directory.EnumerateFiles(mapsPath, "*.vpk"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (Regex.IsMatch(name, @"^(de|cs|ar|dm|training)_[a-zA-Z0-9_]+$") && !name.EndsWith("_vanity"))
                    result.Add(new() { Title = OfficialMaps.Name(name), MapName = name, FilePath = file, Bytes = new FileInfo(file).Length, Official = true });
            }
        return new(result.DistinctBy(m => m.Identity).OrderBy(m => m.Official).ThenBy(m => m.Title).ToList(), warnings);
    }
}

public static class Vpk
{
    // Read only the VPK directory tree; never decompress/read the multi-GB payload.
    public static List<string> ReadMapNames(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt32() != 0x55AA1234) throw new InvalidDataException("不是 VPK 文件");
        var version = reader.ReadUInt32(); var treeBytes = reader.ReadUInt32();
        if (version is not (1 or 2) || treeBytes > 32 * 1024 * 1024) throw new InvalidDataException("不支持的 VPK 索引");
        if (version == 2) stream.Seek(16, SeekOrigin.Current);
        var end = stream.Position + treeBytes;
        if (end > stream.Length) throw new InvalidDataException("VPK 正在下载或不完整");
        string ReadZ()
        {
            var bytes = new List<byte>();
            while (stream.Position < end && bytes.Count < 4096) { var b = reader.ReadByte(); if (b == 0) return Encoding.UTF8.GetString(bytes.ToArray()); bytes.Add(b); }
            throw new InvalidDataException("VPK 索引字符串损坏");
        }
        var maps = new List<string>();
        string? addonInfo = null;
        var archiveSizes = new Dictionary<ushort, long>();
        for (var ext = ReadZ(); ext.Length > 0; ext = ReadZ())
            for (var dir = ReadZ(); dir.Length > 0; dir = ReadZ())
                for (var name = ReadZ(); name.Length > 0; name = ReadZ())
                {
                    if (stream.Position + 18 > end) throw new InvalidDataException("VPK 索引截断");
                    reader.ReadUInt32(); var preload = reader.ReadUInt16(); var archive = reader.ReadUInt16(); var offset = reader.ReadUInt32(); var length = reader.ReadUInt32();
                    if (reader.ReadUInt16() != 0xFFFF) throw new InvalidDataException("VPK entry terminator");
                    if (stream.Position + preload > end) throw new InvalidDataException("VPK preload truncated");
                    var preloadBytes = reader.ReadBytes(preload);
                    if (archive == 0x7FFF && end + offset + length > stream.Length) throw new InvalidDataException("VPK 地图数据尚未下载完整");
                    if (archive != 0x7FFF && length > 0)
                    {
                        if (!archiveSizes.TryGetValue(archive, out var archiveLength))
                        {
                            var prefix = path.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) ? path[..^8] : path[..^4];
                            var part = prefix + "_" + archive.ToString("D3") + ".vpk";
                            if (!File.Exists(part)) throw new InvalidDataException("缺少 VPK 分卷：" + Path.GetFileName(part));
                            archiveLength = new FileInfo(part).Length; archiveSizes[archive] = archiveLength;
                        }
                        if ((long)offset + length > archiveLength) throw new InvalidDataException("VPK 分卷尚未下载完整");
                    }
                    // Workshop addon VPKs normally contain maps/<name>.vpk, not a direct vmap_c.
                    if (ext is "vmap_c" or "vpk" && dir == "maps" && Regex.IsMatch(name, @"^[a-zA-Z0-9_\-]+$")) maps.Add(name);
                    // Playable addons can extend an official map with a secondary
                    // spawn group. Their addoninfo declares the actual entry map.
                    if (ext == "txt" && name == "addoninfo" && dir is " " or "" && (long)preload + length <= 65536)
                    {
                        var resume = stream.Position;
                        byte[] payload;
                        if (archive == 0x7FFF)
                        {
                            stream.Position = end + offset;
                            payload = reader.ReadBytes((int)length);
                            stream.Position = resume;
                        }
                        else
                        {
                            var prefix = path.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) ? path[..^8] : path[..^4];
                            using var part = File.OpenRead(prefix + "_" + archive.ToString("D3") + ".vpk");
                            part.Position = offset;
                            payload = new byte[(int)length];
                            part.ReadExactly(payload);
                        }
                        addonInfo = Encoding.UTF8.GetString(preloadBytes.Concat(payload).ToArray());
                    }
                }
        var declared = DeclaredMaps(addonInfo ?? "");
        if (declared.Count > 0) return declared;
        // A compiled 3D skybox can appear next to the playable map inside an addon.
        return maps.Distinct().Where(n => !n.EndsWith("_skybox", StringComparison.OrdinalIgnoreCase) || !maps.Contains(n[..^7], StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static List<string> DeclaredMaps(string addonInfo)
    {
        // Only read data; never execute map-supplied configuration or scripts.
        var text = Regex.Replace(addonInfo, @"(?m)//[^\r\n]*", "");
        var field = Regex.Match(text, "(?:\\bmaps\\b|\"maps\")\\s*[=:]\\s*\\[(?<maps>[^\\]]*)\\]", RegexOptions.IgnoreCase);
        return Regex.Matches(field.Groups["maps"].Value, "\"(?<name>[a-zA-Z0-9_\\-]+)\"")
            .Select(m => m.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
