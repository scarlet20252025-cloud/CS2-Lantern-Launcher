using System.Text.RegularExpressions;

namespace Lantern.Core;

/// <summary>
/// A running CS2 process and the console channel it was launched with.
/// Since 1.41.7.8 there is no console port or password: the engine opens the
/// named pipes named on its command line, and pipe security is the OS's job.
/// </summary>
public sealed record GameEndpoint(int ProcessId, bool Dedicated, string? CommandPipe)
{
    public int? RequestedMaxPlayers { get; init; }
    public bool HasConsole => CommandPipe != null;
    public override string ToString() => $"PID {ProcessId} · {(Dedicated ? "独立服务器" : "游戏客户端")} · {(CommandPipe ?? "未启用控制台")}";
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
        // -concommandpipe takes "<command>,<output>"; we only ever write to the first.
        var pair = Value("-concommandpipe");
        var command = pair.Split(',')[0].Trim();
        return new(pid, args.Skip(1).Any(a => a.Equals("-dedicated", StringComparison.OrdinalIgnoreCase)),
            IsPipe(command) ? command : null)
        { RequestedMaxPlayers = int.TryParse(Value("-maxplayers"), out var max) && max is >= 2 and <= 64 ? max : null };
    }

    // Only accept the full Win32 form the engine itself requires.
    static bool IsPipe(string value)
        => value.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) && value.Length > 9
        && value[9..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
