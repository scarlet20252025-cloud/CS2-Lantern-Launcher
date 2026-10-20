namespace Lantern.Core;

// Only an explicitly selected workshop package, never an official map or a library root.
public static class MapDeletion
{
    public static string Package(MapEntry map, IEnumerable<string> roots)
    {
        if (map.Official || !map.Local) throw new ArgumentException("只能删除已下载的工坊地图。");
        var id = Workshop.ParseId(map.Id);
        var file = Path.GetFullPath(map.FilePath);
        foreach (var source in roots)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var current = new DirectoryInfo(Path.GetDirectoryName(file)!);
            while (current != null && (current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) || current.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                if (current.Name == id) { Validate(current.FullName, id); return current.FullName; }
                current = current.Parent;
            }
        }
        throw new ArgumentException("地图不在已识别的工坊 ID 目录中，不能安全删除。");
    }
    public static void Validate(string package, string id)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(package));
        if (dir.Name != Workshop.ParseId(id) || !dir.Exists) throw new IOException("工坊目录已变化，请重新扫描。");
        for (var p = dir; p != null; p = p.Parent)
            if ((p.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不能删除经过链接或联接的地图目录。");
        var pending = new Stack<DirectoryInfo>(); pending.Push(dir);
        while (pending.TryPop(out var folder))
            foreach (var item in folder.EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("地图包包含链接，已停止删除。");
                if (item is DirectoryInfo child) pending.Push(child);
            }
    }
}
