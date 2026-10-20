using System.IO;
using System.Management;
using Lantern.Core;

namespace Lantern.App;

public static class GameProcesses
{
    public static GameEndpoint? Find(string root, bool dedicated)
    {
        var expected = Path.GetFullPath(Path.Combine(root, "game", "bin", "win64", "cs2.exe"));
        using var query = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='cs2.exe'");
        using var results = query.Get();
        var matches = new List<GameEndpoint>();
        foreach (ManagementObject process in results)
        {
            using (process)
            {
                var path = process["ExecutablePath"] as string;
                var command = process["CommandLine"] as string;
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(command))
                    throw new InvalidOperationException("无法读取 CS2 进程信息，不能安全判断连接目标。请让游戏与启动器以同一 Windows 用户权限运行。");
                if (!string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase)
                    && !(dedicated && string.Equals(Path.GetFullPath(path), DedicatedRuntime.Executable(root), StringComparison.OrdinalIgnoreCase))) continue;
                var endpoint = GameEndpointParser.Parse(Convert.ToInt32(process["ProcessId"]), command);
                if (endpoint.Dedicated == dedicated) matches.Add(endpoint);
            }
        }
        if (matches.Count > 1) throw new InvalidOperationException("发现多个同类型 CS2 进程，无法确定目标。请先关闭不用的游戏或服务器，再连接；不会自动选择或结束进程。");
        return matches.SingleOrDefault();
    }
}
