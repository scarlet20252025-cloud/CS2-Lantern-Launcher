using System.Text;
using System.Text.Json;

namespace Lantern.Core;

public sealed record RoomStatus(string Map, int Humans, int Bots, string Identity, int Port, string Addon);

/// <summary>One row of the <c>status</c> player table.</summary>
/// <param name="Id">Engine slot. <c>65535</c> is an empty slot, not a player.</param>
/// <param name="Name">Quoted name without quotes; empty for an empty slot.</param>
/// <param name="State"><c>active</c>, <c>spawning</c>, <c>challenging</c>, …</param>
public sealed record PlayerRow(int Id, string Name, string State, bool IsBot)
{
    public bool IsEmptySlot => Id == 65535;
    public bool IsReady => !IsEmptySlot && !IsBot && State == "active";
}

public static class RoomStatusParser
{
    /// <summary>
    /// Reads the <c>status</c> player table into rows.
    ///
    /// Field positions, measured on 1.41.9.1 (2026-10-15 build):
    /// <code>
    ///    id     time ping loss      state   rate adr name
    ///     0    02:02    0    0     active 786432 127.0.0.1:50333 'Mikuo'   ← human
    ///   769      BOT    0    0     active      0 'Comb'                     ← bot: no time/adr
    /// 65535 [NoChan]    0    0 challenging      0unknown ''                 ← empty slot
    /// </code>
    /// The state column is index 4 in every shape, so it is matched by position
    /// rather than by looking for a SteamID: the adr column used to be
    /// <c>[U:1:x]:port</c> and is now a plain <c>ip:port</c>, which is exactly
    /// what made a SteamID-shaped regex silently report "0 ready" for a player
    /// who was already in the map.
    /// </summary>
    public static List<PlayerRow> Players(string text)
    {
        var start = text.IndexOf("---------players--------", StringComparison.Ordinal);
        var end = text.IndexOf("#end", start < 0 ? 0 : start, StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new InvalidOperationException("未读取到完整玩家状态，请稍后再换图。");
        var rows = new List<PlayerRow>();
        foreach (var raw in text[(start + "---------players--------".Length)..end].Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // The name is the last quoted field and may itself contain spaces and
            // even the word BOT, so it is cut off before the columns are counted.
            var quoted = line.LastIndexOf('\'');
            var opened = quoted > 0 ? line.LastIndexOf('\'', quoted - 1) : -1;
            var name = opened >= 0 && quoted > opened ? line[(opened + 1)..quoted] : "";
            var fields = (opened > 0 ? line[..opened] : line).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Header row: "id time ping loss state rate adr name".
            if (fields.Length > 0 && fields[0] == "id") continue;
            if (fields.Length < 5 || !int.TryParse(fields[0], out var id)) continue;
            // A bot's time column reads "BOT" instead of a clock. Older builds put
            // it in the adr column instead, so any bare field counts.
            var isBot = fields.Contains("BOT");
            rows.Add(new PlayerRow(id, name, fields[4], isBot));
        }
        return rows;
    }

    public static int ActiveHumans(string text) => Players(text).Count(p => p.IsReady);

    /// <summary>Humans that are connected but not yet fully in the map.</summary>
    public static List<string> LoadingHumans(string text)
        => Players(text).Where(p => !p.IsEmptySlot && !p.IsBot && p.State != "active")
            .Select(p => p.Name.Length == 0 ? "(未命名)" : p.Name).ToList();
    public static RoomStatus Parse(string text)
    {
        // The engine can append asynchronous output between the closing JSON
        // brace and our echo marker. Consume one complete JSON value only.
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
        using var json = JsonDocument.ParseValue(ref reader);
        if (!json.RootElement.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object
            || !server.TryGetProperty("map", out var map) || map.ValueKind != JsonValueKind.String || (string.IsNullOrWhiteSpace(map.GetString()) || map.GetString() == "<empty>"))
            throw new InvalidOperationException("控制台已连接，但这个服务器尚未加载地图（Inactive）。独立服务器与游戏客户端是两个进程；客户端有画面不代表独立服务器已开图。");
        int Number(string name, int fallback) => server.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;
        string Text(string name) => server.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        int humans = Number("clients_human", -1), bots = Number("clients_bot", -1);
        if (humans < 0 || bots < 0) throw new InvalidOperationException("地图已加载，但人数状态暂未就绪，请稍后重试。");
        return new(map.GetString()!, humans, bots, Text("steamid"), Number("udp_port", 27015), Text("addon"));
    }
}

public static class ConsoleProtocol
{
    public static byte[] Command(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(char.IsControl)) throw new ArgumentException("请输入单行 CS2 命令，不要包含换行或控制字符。");
        var bytes = Encoding.UTF8.GetBytes(command + "\n");
        if (bytes.Length > 8193) throw new ArgumentException("命令最多 8192 UTF-8 字节。");
        return bytes;
    }
}

/// <summary>
/// A console session on a launcher-owned command pipe. Commands are written to
/// the pipe; output arrives on the engine's redirected stdout and is matched
/// here by echo markers. Never replay a mutation after a failure.
/// </summary>
public sealed class GameConsole : IDisposable
{
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim queries = new(1, 1);
    readonly TaskCompletionSource verified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly string probe = "LANTERN_LINK_" + Guid.NewGuid().ToString("N");
    ConsolePipe? pipe;
    // Connect() disposes itself on failure and callers also wrap it in using,
    // so Dispose must be idempotent or teardown throws over the real error.
    int disposed;
    public event Action<string>? Output;
    /// <summary>
    /// Same lines as <see cref="Output"/>, minus protocol traffic (the status_json
    /// dump the room monitor polls every few seconds). The UI log subscribes here.
    /// </summary>
    public event Action<string>? Display;
    public event Action<string>? Disconnected;
    public bool IsReady { get; private set; }
    public string? CommandPipe => pipe?.CommandPath;

    /// <summary>
    /// Binds to the channel a running server was launched with. There is no
    /// password: pipe access is governed by the OS, not a shared secret.
    /// </summary>
    public async Task Connect(string commandPipe, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(commandPipe)) throw new ArgumentException("缺少控制台管道名称。");
        var target = ConsolePipe.Find(commandPipe)
            ?? throw new InvalidOperationException("控制台管道已不存在。启动器退出后无法接管已运行的服务器；请用“重启独立服务器”重新开服。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await target.WaitUntilConnected(TimeSpan.FromSeconds(5), timeout.Token);
            pipe = target;
            pipe.Line += OnLine;
            await Write("echo " + probe);
            await verified.Task.WaitAsync(timeout.Token);
            IsReady = true;
        }
        catch { Dispose(); throw; }
    }

    void OnLine(string text)
    {
        if (text.Trim() == probe) { verified.TrySetResult(); return; }
        if (text.Length == 0) return;
        Output?.Invoke(text);
        if (!Protocol(text)) Display?.Invoke(text);
    }

    /// <summary>
    /// Recognises the machine-readable traffic our own probes generate, so it can
    /// stay out of the operator's log while still reaching the parsers.
    ///
    /// status_json prints a multi-line JSON object; the room monitor asks for one
    /// every few seconds, which would otherwise fill the whole window. Echo
    /// markers are only ever ours, so they are dropped too.
    /// </summary>
    static bool Protocol(string text)
    {
        var trimmed = text.Trim();
        // Echo markers are ours by construction, including the one appended to the
        // closing brace of a JSON dump ("}LANTERN_STATUS_…").
        if (trimmed.Contains("LANTERN_", StringComparison.Ordinal)) return true;
        if (trimmed is "{" or "}") return true;
        if (trimmed.StartsWith("}", StringComparison.Ordinal)) return true;
        // Body of the pretty-printed object: tab-indented "key": value.
        // Not a bare quote check: bot join events also start with a quote
        // ('"Bot3<3><BOT>" switched from team…') and those must stay visible.
        return text.StartsWith("\t", StringComparison.Ordinal);
    }

    async Task Write(string command)
    {
        var target = pipe ?? throw new InvalidOperationException("控制台未连接，请先启动独立服务器。");
        await target.Send(command, lifetime.Token);
    }

    public async Task Send(string command)
    {
        if (!IsReady) throw new InvalidOperationException("控制台未连接，请先启动独立服务器。");
        try { await Write(command); }
        catch { Fail("写入失败"); throw; }
    }

    void Fail(string reason)
    {
        IsReady = false;
        Disconnected?.Invoke(reason);
    }

    public async Task<RoomStatus> ReadStatus()
    {
        await queries.WaitAsync(lifetime.Token);
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new StringBuilder(); bool collecting = false;
        var marker = "LANTERN_STATUS_" + Guid.NewGuid().ToString("N");
        void ReceiveStatus(string line)
        {
            if (line.Trim() == "{") collecting = true;
            int end = line.IndexOf(marker, StringComparison.Ordinal);
            if (collecting) buffer.Append(end < 0 ? line : line[..end]);
            if (end >= 0) done.TrySetResult(buffer.ToString());
        }
        Output += ReceiveStatus;
        try
        {
            await Send("status_json; echo " + marker);
            return RoomStatusParser.Parse(await done.Task.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token));
        }
        finally { Output -= ReceiveStatus; queries.Release(); }
    }
    public async Task<int> ReadActiveHumans()
        => RoomStatusParser.ActiveHumans(await ReadStatusText());

    /// <summary>
    /// The player table, for callers that must know *who* is still loading
    /// rather than only how many are ready.
    /// </summary>
    public async Task<List<PlayerRow>> ReadPlayers()
        => RoomStatusParser.Players(await ReadStatusText());

    public async Task<int?> ReadSpawnCount()
    {
        var text = await ReadStatusText();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^spawn\s*:\s*(\d+)\s*$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : null;
    }

    public async Task<string> ReadStatusText()
    {
        // CS2 may rate-limit repeated status requests. Retry only this read, never a bot mutation.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var text = await ExecuteCaptured("status");
            if (text.Contains("---------players--------") && text.Contains("#end")) return text;
            await Task.Delay(500, lifetime.Token);
        }
        throw new InvalidOperationException("服务器暂未返回完整玩家列表，已停止后续操作，请稍后重试。");
    }

    // Executes a mutation once, then waits for its echo boundary; never retries a mutation.
    public async Task<string> ExecuteCaptured(string command)
    {
        await queries.WaitAsync(lifetime.Token);
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new StringBuilder();
        var marker = "LANTERN_PLAYERS_" + Guid.NewGuid().ToString("N");
        void Collect(string line)
        {
            int end = line.IndexOf(marker, StringComparison.Ordinal);
            buffer.Append(end < 0 ? line : line[..end]);
            if (end >= 0) done.TrySetResult(buffer.ToString());
        }
        Output += Collect;
        try
        {
            await Send(command + "; echo " + marker);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
        }
        finally { Output -= Collect; queries.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        IsReady = false;
        if (pipe != null) { pipe.Line -= OnLine; pipe = null; }
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

public static class QuickCommands
{
    public async static Task FillTeams(GameConsole console, int humansT, int humansCt, int size)
    {
        var plan = TeamFill(humansT, humansCt, size);
        var before = await console.ReadStatus();
        if (before.Humans != humansT + humansCt) throw new InvalidOperationException("真人人数与填写不一致，未调整人机。");
        await console.Send("mp_autoteambalance 0; mp_limitteams 0; bot_auto_vacate 0; bot_quota_mode normal; bot_quota 0; bot_kick");
        await WaitBots(console, 0, before);
        foreach (var (team, count) in new[] { ("t", plan.T), ("ct", plan.Ct) })
        {
            if (count == 0) continue;
            var result = await TeamBots.Add(console, team, count, 64);
            if (result.Added != count) throw new InvalidOperationException($"{team.ToUpperInvariant()} 实际添加 {result.Added}/{count}：{result.Detail}");
        }
    }
    public static async Task WaitBots(GameConsole console, int count, RoomStatus? expected = null)
    {
        int stable = 0; RoomStatus? actual = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500); actual = await console.ReadStatus();
            if (expected != null && (actual.Map != expected.Map || actual.Humans != expected.Humans))
                throw new InvalidOperationException("地图或真人人数变化，已停止后续补位，请重新填写。");
            stable = actual.Bots == count ? stable + 1 : 0;
            if (stable >= 3) return;
        }
        throw new InvalidOperationException($"人机目标 {count} 未达到，实际 {actual?.Bots}。请查看游戏人数／出生点限制，未继续下一步。");
    }
    public static (int Size, int T, int Ct) TeamFill(int humansT, int humansCt, int targetSize = 0)
    {
        if (humansT is < 0 or > 32 || humansCt is < 0 or > 32 || humansT + humansCt == 0)
            throw new ArgumentException("两队真人人数应为 0–32，且至少有一名真人。");
        if (targetSize is < 0 or > 32 || targetSize > 0 && targetSize < Math.Max(humansT, humansCt))
            throw new ArgumentException("每队目标必须容纳现有真人，范围 1–32；0 表示按人数较多的一队补齐。");
        int size = targetSize == 0 ? Math.Max(humansT, humansCt) : targetSize, t = size - humansT, ct = size - humansCt;
        return (size, t, ct);
    }
    public const string RemoveBots = "bot_quota 0; bot_kick";
    public const string Restart = "mp_restartgame 1";
    public static string Balance(int totalPlayers)
    {
        if (totalPlayers is < 2 or > 64) throw new ArgumentException("补位总人数需为 2–64（包含玩家）。");
        return $"mp_autoteambalance 1; mp_limitteams 0; bot_auto_vacate 0; bot_join_team any; bot_quota_mode fill; bot_quota {totalPlayers}";
    }
}
