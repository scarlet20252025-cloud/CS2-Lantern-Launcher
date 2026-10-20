using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lantern.Core;

public sealed record RoomStatus(string Map, int Humans, int Bots, string Identity, int Port, string Addon);

public static class RoomStatusParser
{
    public static int ActiveHumans(string text)
    {
        if (!text.Contains("---------players--------") || !text.Contains("#end"))
            throw new InvalidOperationException("未读取到完整玩家状态，请稍后再换图。");
        return System.Text.RegularExpressions.Regex.Matches(text,
            @"^\s*\d+\s+\S+\s+\S+\s+\S+\s+active\s+\S+\s+\[U:\d+:\d+\](?::\d+)?\s",
            System.Text.RegularExpressions.RegexOptions.Multiline).Count;
    }
    public static RoomStatus Parse(string text)
    {
        // NetCon can append asynchronous networking output between the closing
        // JSON brace and our echo marker. Consume one complete JSON value only.
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
        using var json = JsonDocument.ParseValue(ref reader);
        if (!json.RootElement.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object
            || !server.TryGetProperty("map", out var map) || map.ValueKind != JsonValueKind.String || (string.IsNullOrWhiteSpace(map.GetString()) || map.GetString() == "<empty>"))
            throw new InvalidOperationException("终端已连接，但这个服务器尚未加载地图（Inactive）。独立服务器与游戏客户端是两个进程；客户端有画面不代表独立服务器已开图。");
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

// Authenticated Source NetCon on loopback. Never replay commands after disconnect.
public sealed class GameConsole : IDisposable
{
    readonly TcpClient client = new();
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim writer = new(1, 1);
    readonly SemaphoreSlim queries = new(1, 1);
    readonly TaskCompletionSource verified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly string probe = "LANTERN_LINK_" + Guid.NewGuid().ToString("N");
    string secret = "";
    public event Action<string>? Output;
    public event Action<string>? Disconnected;
    public bool IsReady { get; private set; }
    public async Task Connect(int port = 2121, string password = "")
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("终端端口应为 1024–65535。");
        if (password.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("终端密码必须为字母或数字。");
        secret = password;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            _ = Task.Run(Receive);
            if (password.Length > 0) await Write("PASS " + password);
            await Write("echo " + probe);
            await verified.Task.WaitAsync(timeout.Token);
            IsReady = true;
        }
        catch { Dispose(); throw; }
    }
    async Task Receive()
    {
        string reason = "游戏连接已关闭";
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, true);
            var buffer = new char[4096]; var line = new StringBuilder();
            while (!lifetime.IsCancellationRequested)
            {
                int count = await reader.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0) break;
                for (int i = 0; i < count; i++)
                {
                    char c = buffer[i]; if (c == '\r') continue;
                    if (c != '\n') { line.Append(c); if (line.Length < 65536) continue; }
                    var text = line.ToString(); line.Clear();
                    if (text.Trim() == probe) { verified.TrySetResult(); continue; }
                    if (text.Length > 0) Output?.Invoke((secret.Length > 0 ? text.Replace(secret, "<hidden>") : text) + "\n");
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { reason = e.Message; }
        finally { IsReady = false; verified.TrySetException(new IOException(reason)); Disconnected?.Invoke(reason); }
    }
    async Task Write(string command)
    {
        var packet = ConsoleProtocol.Command(command);
        await writer.WaitAsync(lifetime.Token);
        try { await client.GetStream().WriteAsync(packet, lifetime.Token); }
        finally { writer.Release(); }
    }
    public async Task Send(string command)
    {
        if (!IsReady) throw new InvalidOperationException("终端未连接，请先连接本机游戏。");
        try { await Write(command); }
        catch { Dispose(); throw; }
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
    public void Dispose() { IsReady = false; lifetime.Cancel(); client.Dispose(); }
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
