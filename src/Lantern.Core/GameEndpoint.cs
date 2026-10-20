using System.Text.RegularExpressions;

namespace Lantern.Core;

public sealed record GameEndpoint(int ProcessId, bool Dedicated, int? Port, string Password)
{
    public int? RequestedMaxPlayers { get; init; }
    // Do not expose process authentication material in diagnostics.
    public override string ToString() => $"PID {ProcessId} · {(Dedicated ? "独立服务器" : "游戏客户端")} · {Port?.ToString() ?? "未启用终端"}";
}

public static class GameEndpointParser
{
    public static GameEndpoint Parse(int pid, string commandLine)
    {
        var args = Regex.Matches(commandLine, "\"[^\"]*\"|[^\\s]+").Select(m => m.Value.Trim('"')).ToArray();
        string Value(string option)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : "";
                if (args[i].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(option.Length + 1)..];
            }
            return "";
        }
        var port = Value("-netconport");
        return new(pid, args.Skip(1).Any(a => a.Equals("-dedicated", StringComparison.OrdinalIgnoreCase)),
            int.TryParse(port, out var n) && n is >= 1024 and <= 65535 ? n : null, Value("-netconpassword"))
        { RequestedMaxPlayers = int.TryParse(Value("-maxplayers"), out var max) && max is >= 2 and <= 64 ? max : null };
    }
}
