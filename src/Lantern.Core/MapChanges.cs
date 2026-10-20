using System.Text;
using System.Security.Cryptography;

namespace Lantern.Core;

public static class MapChanges
{
    // Poll metadata only: no payload reads, watchers, handles or work proportional to VPK size.
    public static string Fingerprint(Installation install, IEnumerable<string> extras, IEnumerable<string>? metadataFiles = null)
    {
        var entries = new List<string>();
        foreach (var root in Discovery.WorkshopRoots(install, extras))
        {
            entries.Add(root + ":" + Directory.Exists(root));
            if (!Directory.Exists(root)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 5 };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (!file.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) && !file.EndsWith("publish_data.txt", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file); entries.Add(file + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks);
            }
        }
        foreach (var library in install.Libraries)
        {
            var file = new FileInfo(Path.Combine(library, "steamapps", "workshop", "appworkshop_730.acf"));
            if (file.Exists) entries.Add(file.FullName + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks);
        }
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var path in metadataFiles ?? Array.Empty<string>())
        {
            var file = new FileInfo(path);
            entries.Add(path + ":" + (file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
    }
}
