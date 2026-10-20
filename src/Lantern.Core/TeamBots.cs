using System.Text.RegularExpressions;

namespace Lantern.Core;

public sealed record TeamAddResult(int Added, int Requested, string Team, string Detail);

public static class TeamBots
{
    public static bool IsExactAddition(HashSet<int> before, HashSet<int> after, int slot)
    {
        var added = after.Except(before).ToArray();
        return after.Count == before.Count + 1 && added.Length == 1 && (added[0] & 255) == slot && before.IsSubsetOf(after);
    }
    public static string Command(string team)
        => team is "t" or "ct" ? "bot_add_" + team : throw new ArgumentException("队伍必须是 T 或 CT。");

    public static HashSet<int> BotIds(string status)
    {
        if (!status.Contains("---------players--------") || !status.Contains("#end"))
            throw new InvalidOperationException("玩家列表不完整，未继续添加。");
        return Regex.Matches(status, @"(?m)^\s*(\d+)\s+BOT\s").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
    }

    public static (int Id, string Team)? Joined(string output)
    {
        var match = Regex.Match(output, "<(?<id>\\d+)><BOT>\" switched from team <Unassigned> to <(?<team>CT|TERRORIST)>");
        return match.Success ? (int.Parse(match.Groups["id"].Value), match.Groups["team"].Value == "CT" ? "ct" : "t") : null;
    }

    public static bool ConfirmSurvivor(HashSet<int> before, HashSet<int> after,
        IReadOnlyDictionary<int, string> observedTeams, string team)
    {
        var added = after.Except(before).ToArray();
        return after.Count == before.Count + 1 && added.Length == 1 && before.IsSubsetOf(after)
            && observedTeams.TryGetValue(added[0] & 255, out var actualTeam) && actualTeam == team;
    }

    public static async Task<TeamAddResult> Add(GameConsole console, string team, int count, int capacity, IProgress<int>? progress = null)
    {
        var command = Command(team);
        if (count is < 1 or > 32) throw new ArgumentException("每次添加数量应为 1–32。");
        var initial = await console.ReadStatus();
        if (initial.Humans == 0 && (await console.ReadStatusText()).Contains("(hibernating)", StringComparison.Ordinal))
            return new(0, count, team, "服务器正在空房休眠，请先加入游戏再添加人机。");
        if (initial.Humans + initial.Bots + count > capacity)
            throw new ArgumentException($"添加后超过当前进程的人数上限 {capacity}，未执行。请减少数量或调整启动人数后重启服务器。");
        var logging = await console.ExecuteCaptured("log");
        bool restoreLogging = logging.Contains("not currently logging", StringComparison.OrdinalIgnoreCase);
        int added = 0;
        var observedTeams = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        void Observe(string line)
        {
            var joined = Joined(line);
            if (joined != null) observedTeams[joined.Value.Id] = joined.Value.Team;
        }
        console.Output += Observe;
        try
        {
            await console.ExecuteCaptured($"log on; mp_autoteambalance 0; mp_limitteams 0; bot_auto_vacate 0; bot_quota_mode normal; bot_quota {initial.Bots}; bot_join_team {team.ToUpperInvariant()}");
            for (; added < count; added++)
            {
                var before = await console.ReadStatus();
                if (before.Map != initial.Map || before.Addon != initial.Addon || before.Identity != initial.Identity || before.Humans != initial.Humans || before.Bots != initial.Bots + added)
                    return new(added, count, team, "房间或人数发生变化，已停止后续添加。");
                var idsBefore = BotIds(await console.ReadStatusText());
                if (idsBefore.Count != before.Bots)
                    return new(added, count, team, "服务器休眠或玩家列表尚未就绪，请先加入游戏再添加；未继续执行。");
                observedTeams.Clear();
                // Keep quota correction in the same command buffer, before the next
                // simulation frame can create a second bot and kick an arbitrary one.
                var output = await console.ExecuteCaptured($"{command}; bot_quota {before.Bots + 1}");
                // Unlike quota growth, explicit bot_add_ct/t refuses a full team instead of filling the other side.
                if (output.Contains("Team is full", StringComparison.OrdinalIgnoreCase))
                {
                    await console.ExecuteCaptured($"bot_quota {before.Bots}");
                    await Task.Delay(300);
                    var remaining = BotIds(await console.ReadStatusText());
                    return new(added, count, team, remaining.SetEquals(idsBefore)
                        ? "目标队伍已满（模式或地图出生点限制）；人数未增加。启动人数上限不等于单队容量。"
                        : "目标队伍已满，已撤销配额增长；玩家列表发生变化，请核对终端人数后再操作。");
                }
                var joined = Joined(output);
                if (joined == null || joined.Value.Team != team)
                {
                    await console.Send($"bot_quota {before.Bots}");
                    return new(added, count, team, "未获得指定队伍入队确认，已停止添加并撤销配额增长。请查看终端输出。");
                }
                int stable = 0;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    await Task.Delay(200);
                    var actual = await console.ReadStatus();
                    var ids = BotIds(await console.ReadStatusText());
                    bool unchanged = actual.Map == before.Map && actual.Addon == before.Addon && actual.Identity == before.Identity && actual.Humans == before.Humans;
                    // status includes the userid generation in its high byte; join logs use the slot.
                    // Quota correction can remove the first bot and retain a second one created
                    // by the engine in the same frame. Verify the survivor's actual team event.
                    bool exact = actual.Bots == before.Bots + 1 && ConfirmSurvivor(idsBefore, ids, observedTeams, team);
                    if (!unchanged) return new(added, count, team, "核验时房间发生变化，已停止。最后一次加人结果待核对。");
                    stable = exact ? stable + 1 : 0;
                    if (stable >= 2) break;
                }
                if (stable < 2) return new(added, count, team, "引擎人数未保持预期值，已停止，最后一次加人结果待核对；不会自动重发。");
                progress?.Report(added + 1);
            }
            return new(added, count, team, "已确认新增人机加入指定队伍，自动平衡与持续填充已关闭。");
        }
        finally
        {
            console.Output -= Observe;
            if (restoreLogging && console.IsReady) await console.Send("log off");
        }
    }
}
