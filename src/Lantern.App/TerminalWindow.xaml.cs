using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lantern.Core;

namespace Lantern.App;

public partial class TerminalWindow : Window
{
    GameConsole? connection;
    readonly ConcurrentQueue<string> pending = new();
    readonly DispatcherTimer flush = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly List<string> history = new();
    int historyIndex;
    bool connecting, closed;
    long mapActivation;
    readonly object diagnosticLock = new();
    string diagnosticTail = "";
    public string FailureOutput { get { lock (diagnosticLock) return diagnosticTail; } }
    public void ClearFailureOutput() { lock (diagnosticLock) diagnosticTail = ""; }
    readonly SemaphoreSlim mapActivity = new(0, 1);
    public Task<bool> WaitForMapActivity(int milliseconds, CancellationToken token) => mapActivity.WaitAsync(milliseconds, token);
    public long MapActivation => Interlocked.Read(ref mapActivation);
    readonly string password;
    readonly string gameRoot;
    readonly bool dedicated;
    public bool Dedicated => dedicated;
    public bool IsClosed => closed;
    public Func<Task>? EnableTerminalRequested { get; set; }
    public void ReportRepair(string message) { ActionState.Text = message; Log(message); }
    public void ResetConnection() { connection?.Dispose(); connection = null; }
    async void EnableTerminalClick(object sender, RoutedEventArgs e)
    {
        if (EnableTerminalRequested == null) return;
        RepairButton.IsEnabled = false;
        try { await EnableTerminalRequested(); }
        catch (Exception ex) { ReportRepair("未完成 · " + ex.Message); }
        finally { if (!closed) RepairButton.IsEnabled = true; }
    }
    public bool IsConnected => connection?.IsReady == true;
    public async Task<RoomStatus> GetRoomStatus() { await ConnectGame(); return await connection!.ReadStatus(); }
    public async Task<int> GetActiveHumans() { await ConnectGame(); return await connection!.ReadActiveHumans(); }
    public async Task<int?> GetSpawnCount() { await ConnectGame(); return await connection!.ReadSpawnCount(); }
    public int TargetPort => int.TryParse(PortInput.Text, out var port) ? port : 0;
    public TerminalWindow(string password, string gameRoot, bool dedicated)
    {
        this.password = password;
        this.gameRoot = gameRoot;
        this.dedicated = dedicated;
        InitializeComponent();
        BuildManagementPanels();
        RepairPanel.Visibility = dedicated ? Visibility.Collapsed : Visibility.Visible;
        TeamCountsChanged(this, null!);
        PortInput.Text = "自动"; PortInput.IsReadOnly = true;
        flush.Tick += (_, _) => Flush(); flush.Start();
        Closed += (_, _) => { closed = true; connection?.Dispose(); flush.Stop(); };
        Log("连接目标：" + (dedicated ? "独立服务器" : "游戏客户端") + "。自动读取该进程的实际终端端口；不会重启游戏、切换地图或连接另一种进程。游戏 UDP 端口 27015 与终端端口不是一回事。");
    }
    void Log(string text)
    {
        if (closed) return;
        lock (diagnosticLock)
        {
            diagnosticTail += text;
            if (diagnosticTail.Length > 32768) diagnosticTail = diagnosticTail[^32768..];
        }
        if (text.Contains("Host activate: ", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref mapActivation);
            try { mapActivity.Release(); } catch (SemaphoreFullException) { }
        }
        if (pending.Count >= 1000) pending.TryDequeue(out _);
        pending.Enqueue(text.TrimEnd('\r', '\n') + "\n");
    }
    void Flush()
    {
        var batch = new System.Text.StringBuilder();
        for (int i = 0; i < 250 && pending.TryDequeue(out var line); i++) batch.Append(line);
        if (batch.Length == 0) return;
        if (LogBox.Text.Length > 180000) LogBox.Text = LogBox.Text[^90000..];
        LogBox.AppendText(batch.ToString()); LogBox.ScrollToEnd();
    }
    public async Task ConnectGame()
    {
        if (IsConnected) return;
        if (connecting) throw new InvalidOperationException("正在连接，请稍候。");
        connecting = true; ConnectButton.IsEnabled = false; ConnectionText.Text = "正在连接并验证游戏回显…";
        GameEndpoint endpoint;
        try
        {
            endpoint = await Task.Run(() => GameProcesses.Find(gameRoot, dedicated))
                ?? throw new InvalidOperationException("没有找到" + (dedicated ? "独立服务器。请确认开服方式；正在玩的客户端不是独立服务器。" : "游戏客户端，请先通过启动器启动游戏。"));
            if (closed) throw new ObjectDisposedException(nameof(TerminalWindow));
            if (endpoint.Port == null) throw new InvalidOperationException("当前" + (dedicated ? "独立服务器" : "游戏客户端") + "未启用外部终端，并非端口改变。" + (dedicated ? "请正常关闭独立服务器后重新开服。" : "打完后点击上方“启用终端并重新启动 CS2”，确认后生效。现在可在游戏控制台手动执行命令。"));
            PortInput.Text = endpoint.Port.Value.ToString();
        }
        catch (Exception ex)
        {
            ConnectionText.Text = "未连接 · 目标进程尚不具备终端连接条件";
            ActionState.Text = ex.Message;
            connecting = false; ConnectButton.IsEnabled = true; throw;
        }
        int port = endpoint.Port.Value;
        connection?.Dispose(); var current = new GameConsole(); connection = current;
        current.Output += Log;
        current.Disconnected += message =>
        {
            if (!closed) Dispatcher.BeginInvoke(() => { if (ReferenceEquals(connection, current)) { ConnectionText.Text = "已断开"; PortInput.IsEnabled = true; Log("连接结束：" + message); } });
        };
        try
        {
            await current.Connect(port, endpoint.Password);
            if (closed) return;
            ConnectionText.Text = $"已验证 · {(dedicated ? "独立服务器" : "游戏客户端")} · PID {endpoint.ProcessId} · 127.0.0.1:{port}";
            PortInput.IsEnabled = false;
            RepairPanel.Visibility = Visibility.Collapsed;
            Log("[连接] 游戏已返回验证回显。下方将显示游戏控制台输出。");
        }
        catch { ConnectionText.Text = $"未连接 · PID {endpoint.ProcessId} 的终端 {port} 尚未就绪或验证失败，请稍后重试"; throw; }
        finally { connecting = false; ConnectButton.IsEnabled = true; }
    }
    public async Task SendCommand(string command)
    {
        await ConnectGame();
        Log("> " + command);
        await connection!.Send(command);
        ActionState.Text = "指令已发送 · 执行结果请查看游戏回显";
        Log("[已发送] 等待游戏输出；换图期间连接可能暂时中断。");
    }
    async Task Run(string command)
    {
        if (applyingPopulation) { Log("正在核实人数，请等待完成后再执行其他命令。"); return; }
        try { await SendCommand(command); }
        catch (Exception ex) { ActionState.Text = "未完成 · " + ex.Message; Log("[未完成] " + ex.Message); }
    }
    async void ConnectClick(object sender, RoutedEventArgs e) { try { await ConnectGame(); } catch (Exception ex) { Log("[连接失败] " + ex.Message); } }
    void DisconnectClick(object sender, RoutedEventArgs e) { connection?.Dispose(); ConnectionText.Text = "已断开"; PortInput.IsEnabled = true; }
    async void SendClick(object sender, RoutedEventArgs e)
    {
        var text = CommandInput.Text.Trim(); if (text.Length == 0) return;
        if (history.Count == 0 || history[^1] != text) { history.Add(text); if (history.Count > 100) history.RemoveAt(0); }
        historyIndex = history.Count; CommandInput.Clear(); await Run(text);
    }
    void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SendClick(sender, e); }
        else if (e.Key is Key.Up or Key.Down && history.Count > 0)
        { e.Handled = true; historyIndex = Math.Clamp(historyIndex + (e.Key == Key.Up ? -1 : 1), 0, history.Count); CommandInput.Text = historyIndex == history.Count ? "" : history[historyIndex]; CommandInput.CaretIndex = CommandInput.Text.Length; }
    }
    async void BalanceClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BalanceTotal.Text, out var total)) { Log("请输入包含玩家在内的补位总人数。"); return; }
        try { await ApplyPopulation(QuickCommands.Balance(total), total); } catch (Exception ex) { ActionState.Text = "未完成 · " + ex.Message; Log(ex.Message); }
    }
    async void RemoveBotsClick(object sender, RoutedEventArgs e) => await Run(QuickCommands.RemoveBots);
    void TeamCountsChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (TeamPreview == null) return;
        try { var p = FillPlan(); TeamPreview.Text = $"→ {p.Size} v {p.Size} · T +{p.T} / CT +{p.Ct}"; TeamFillButton.IsEnabled = true; }
        catch (ArgumentException) { TeamPreview.Text = "填写 0–32 的真人人数"; TeamFillButton.IsEnabled = false; }
    }
    void TargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => TeamCountsChanged(sender, null!);
    (int Size, int T, int Ct) FillPlan()
    {
        if (!int.TryParse(HumansT.Text, out var t) || !int.TryParse(HumansCt.Text, out var ct)) throw new ArgumentException("请填写两队真人人数。");
        var target = TeamTarget.SelectedItem as System.Windows.Controls.ComboBoxItem;
        return QuickCommands.TeamFill(t, ct, int.TryParse(target?.Tag?.ToString(), out var n) ? n : 0);
    }
    async void TeamFillClick(object sender, RoutedEventArgs e)
    {
        if (applyingPopulation) return;
        TeamFillButton.IsEnabled = false;
        try
        {
            var p = FillPlan(); await ConnectGame();
            var status = await connection!.ReadStatus();
            int humans = int.Parse(HumansT.Text) + int.Parse(HumansCt.Text);
            if (humans != status.Humans) throw new ArgumentException($"填写了 {humans} 名已入队真人，但当前有 {status.Humans} 名真人连接。请等所有人加载完成并入队，再按记分板填写；连接中的玩家和观战者不能算已入队。");
            applyingPopulation = true;
            try
            {
                await QuickCommands.FillTeams(connection!, int.Parse(HumansT.Text), int.Parse(HumansCt.Text), p.Size);
                var actual = await connection!.ReadStatus();
                if (actual.Humans != humans) throw new InvalidOperationException("真人人数变化，请按记分板重新补位。");
                ActionState.Text = $"已分阶段补齐：T 人机 {p.T} / CT 人机 {p.Ct}，连接总数 {actual.Humans + actual.Bots}。请核对真人已在所填队伍。";
            }
            finally { applyingPopulation = false; }
        }
        catch (Exception ex) { ActionState.Text = "未完成 · " + ex.Message; Log(ex.Message); }
        finally { TeamCountsChanged(this, null!); }
    }
    bool applyingPopulation;
    async void AddTeamClick(object sender, RoutedEventArgs e)
    {
        if (applyingPopulation) return;
        var team = (string)((System.Windows.Controls.Button)sender).Tag;
        var input = team == "t" ? AddTCount : AddCtCount;
        if (!int.TryParse(input.Text, out var count) || count is < 1 or > 32)
        { ActionState.Text = "添加数量请填写 1–32 的整数。"; return; }
        applyingPopulation = true; CommandTabs.IsEnabled = false;
        ActionState.Text = $"正在向 {team.ToUpperInvariant()} 添加 {count} 名人机，逐个核验…";
        try
        {
            await ConnectGame();
            var endpoint = await Task.Run(() => GameProcesses.Find(gameRoot, dedicated));
            var progress = new Progress<int>(done => { if (!closed && applyingPopulation) ActionState.Text = $"{team.ToUpperInvariant()} 已添加 {done}/{count} 人，正在继续核验…"; });
            var result = await TeamBots.Add(connection!, team, count, endpoint?.RequestedMaxPlayers ?? 64, progress);
            ActionState.Text = $"{team.ToUpperInvariant()} 已添加 {result.Added}/{result.Requested} 人。{result.Detail}";
            Log(ActionState.Text);
        }
        catch (Exception ex) { ActionState.Text = "添加已停止 · " + ex.Message; Log(ActionState.Text); }
        finally { applyingPopulation = false; if (!closed) CommandTabs.IsEnabled = true; }
    }
    async Task ApplyPopulation(string command, int total, int? expectedHumans = null)
    {
        if (applyingPopulation) throw new InvalidOperationException("正在核实上一次人数调整，请稍候。");
        applyingPopulation = true;
        try
        {
            await ConnectGame();
            var before = await connection!.ReadStatus();
            if (total < before.Humans) throw new ArgumentException("目标总人数少于已连接真人，未执行调整。");
            var endpoint = await Task.Run(() => GameProcesses.Find(gameRoot, dedicated));
            if (endpoint?.RequestedMaxPlayers is int cap && total > cap)
                throw new InvalidOperationException($"目标 {total} 人超过当前进程请求的上限 {cap}。请在主界面把人数上限改为至少 {total}，正常重启对应游戏／服务器后再应用。此次未清除人机；修改输入框或换图不会更新旧进程。");
            await SendCommand(command);
            int stable = 0; var actual = before;
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(800);
                if (closed) return;
                actual = await connection.ReadStatus();
                if (actual.Map != before.Map) throw new InvalidOperationException("核实期间地图变化，请在新地图重新应用。");
                if (expectedHumans.HasValue && actual.Humans != expectedHumans.Value)
                    throw new InvalidOperationException("核实期间真人人数变化，请重新填写两队人数再补齐。");
                stable = actual.Humans + actual.Bots == total ? stable + 1 : 0;
                if (stable >= 3)
                {
                    ActionState.Text = $"连接总数已核实：{total} 人（真人 {actual.Humans} / 人机 {actual.Bots}）。请核对两队记分板；加载中／观战者也计入连接数，不能据此保证每队满员。";
                    return;
                }
            }
            ActionState.Text = $"未达到目标 {total} 人：实际真人 {actual.Humans} + 人机 {actual.Bots}。请查看终端拒绝原因，检查模式人数限制、地图出生点及地图脚本；不会报告补齐成功。";
        }
        finally { applyingPopulation = false; }
    }
    async void QuickClick(object sender, RoutedEventArgs e) => await Run((string)((System.Windows.Controls.Button)sender).Tag);
    async void DifficultyClick(object sender, RoutedEventArgs e) => await Run("bot_difficulty " + QuickDifficulty.SelectedIndex);
    void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "文本日志|*.txt", FileName = "Lantern-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { Flush(); File.WriteAllText(dialog.FileName, LogBox.Text); ActionState.Text = "日志已保存"; }
        catch (IOException ex) { ActionState.Text = "保存失败 · " + ex.Message; }
    }
    async void RestartClick(object sender, RoutedEventArgs e) => await Run(QuickCommands.Restart);
    async void StatusClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ConnectGame(); var status = await connection!.ReadStatus();
            ActionState.Text = $"{status.Map} · 真人 {status.Humans} · 人机 {status.Bots}";
        }
        catch (Exception ex) { ActionState.Text = "读取状态失败 · " + ex.Message; }
    }
    void ClearClick(object sender, RoutedEventArgs e) { while (pending.TryDequeue(out _)) { } LogBox.Clear(); }
    public async Task VerifyAndCapture(string path)
    {
        await ConnectGame();
        var marker = "LANTERN_UI_" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutput(string line) { if (line.Trim() == marker) received.TrySetResult(); }
        connection!.Output += OnOutput;
        try { await SendCommand("echo " + marker); await received.Task.WaitAsync(TimeSpan.FromSeconds(4)); }
        finally { connection.Output -= OnOutput; }
        var status = await connection.ReadStatus();
        ActionState.Text = $"{status.Map} · 真人 {status.Humans} · 人机 {status.Bots}";
        Flush(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
        var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
        File.WriteAllText(Path.ChangeExtension(path, ".txt"), "PASS terminal window -> authenticated NetCon -> real game echo -> UI log -> status_json\n");
    }
}
