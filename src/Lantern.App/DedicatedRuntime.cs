using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Lantern.App;

/// <summary>Private executable directory for insecure community servers; client files stay intact.</summary>
public static class DedicatedRuntime
{
    public static string Root(string gameRoot) => Path.Combine(Path.GetFullPath(gameRoot), "LanternDedicated");
    public static string Executable(string gameRoot) => Path.Combine(Root(gameRoot), "game", "bin", "win64", "cs2.exe");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    public static string Prepare(string gameRoot)
    {
        var source = Path.Combine(Path.GetFullPath(gameRoot), "game");
        var destination = Path.Combine(Root(gameRoot), "game");
        // Refuse redirected runtime parents: file maintenance must stay in our private directory.
        foreach (var path in new[] { Root(gameRoot), destination, Path.Combine(destination, "bin"), Path.Combine(destination, "bin", "win64") })
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("服务器运行目录被重定向，停止维护：" + path);
            Directory.CreateDirectory(path);
        }
        var junctions = new List<(string Source, string Destination)>();
        foreach (var directory in Directory.EnumerateDirectories(source))
            if (Path.GetFileName(directory) != "bin") junctions.Add((directory, Path.Combine(destination, Path.GetFileName(directory))));
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(source, "bin", "win64")))
            junctions.Add((directory, Path.Combine(destination, "bin", "win64", Path.GetFileName(directory))));
        var commands = new StringBuilder("$ErrorActionPreference='Stop';\n");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        foreach (var (from, to) in junctions)
        {
            var existing = new DirectoryInfo(to);
            if (existing.Exists)
            {
                var target = existing.ResolveLinkTarget(false);
                if (target == null || !string.Equals(Path.GetFullPath(target.FullName), Path.GetFullPath(from), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("服务器资源链接不匹配，未覆盖：" + to);
            }
            else commands.AppendLine($"New-Item -ItemType Junction -Path {Quote(to)} -Target {Quote(from)} | Out-Null;");
        }
        if (commands.Length > 40)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(commands.ToString())) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("无法建立服务器资源链接。");
            var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("建立服务器资源链接失败：" + error);
        }
        foreach (var relative in new[] { "", "bin", Path.Combine("bin", "win64") })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, relative)))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("vpk.signatures", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, relative, name);
            var current = new FileInfo(target); var original = new FileInfo(file);
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("服务器文件被重定向：" + target);
            // Steam replaces binaries during updates. Relink changed files before the next start.
            if (current.Exists && current.Length == original.Length && current.LastWriteTimeUtc == original.LastWriteTimeUtc) continue;
            if (current.Exists) File.Delete(target);
            if (!CreateHardLink(target, file, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "建立服务器文件硬链接失败：" + name);
        }
        var signatures = Path.Combine(destination, "bin", "win64", "vpk.signatures");
        if (File.Exists(signatures)) throw new IOException("隔离服务器目录存在 vpk.signatures，可能导致工坊资源误校验。请检查该运行目录。");
        return Executable(gameRoot);
    }
}
