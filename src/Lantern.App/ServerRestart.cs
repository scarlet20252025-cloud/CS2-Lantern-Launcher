using System.Diagnostics;
using System.IO;
using System.Windows;
using Lantern.Core;

namespace Lantern.App;

public partial class MainWindow
{
    bool serverExitedDuringLaunch;
    const string ServerExitedMessage = "独立服务器进程已退出，已停止等待核验。请点击“重启独立服务器…”恢复；若总在同一张地图退出，请先换一张已验证的地图。游戏客户端仍在运行，不代表服务器还在。";
    void ReportServerExited()
    {
        if (launching) serverExitedDuringLaunch = true;
        InvalidateRoom("服务器已退出，旧加入信息不可用。");
        RunText.Text = ServerExitedMessage;
        Status("独立服务器已退出 · 可点击重启独立服务器");
    }

    async Task RecoverOnSafeMap(string failedTitle)
    {
        // One recovery attempt only: never reload a map that just terminated the engine.
        var root = RootBox.Text.Trim();
        if (await Task.Run(() => GameProcesses.Find(root, true)) != null)
            throw new InvalidOperationException("已有独立服务器运行，取消重复恢复。");
        var output = terminal?.FailureOutput ?? "";
        var failure = output.LastIndexOf("FATAL ERROR:", StringComparison.Ordinal);
        var detail = failure >= 0 ? output[failure..] : "引擎进程退出；终端未捕获致命错误，请检查游戏崩溃转储。";
        var logPath = System.IO.Path.Combine(dataDir, "server-failure-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".txt");
        System.IO.File.WriteAllText(logPath, $"地图：{failedTitle}\n{detail}");
        terminal?.ResetConnection();
        var safeMap = maps.First(m => m.Official && m.MapName == "de_dust2");
        var settings = Settings() with { ModeIndex = 1, Bots = 0, ApplyRules = true };
        var plan = Hosting.Build(safeMap, settings, "recovery_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
        Hosting.WriteConfigs(root, plan);
        if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(p => p.Port == settings.Port))
            throw new InvalidOperationException("恢复端口仍被占用；未启动第二个服务器。");
        RunText.Text = failedTitle + " 导致服务器退出；正在用 Dust II 恢复（仅尝试一次）…";
        var serverExe = await Task.Run(() => DedicatedRuntime.Prepare(root));
        localServer?.Dispose();
        localServer = LocalServer.Start(serverExe, plan, Path.GetDirectoryName(serverExe)!);
        var process = localServer.Process;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, lifetime.Token);
            if (process.HasExited) throw new InvalidOperationException("Dust II 恢复进程也退出了，已停止自动尝试。诊断：" + logPath);
            RoomStatus actual;
            try { actual = await Terminal().GetRoomStatus(); }
            catch (Exception) { continue; }
            if (!RoomTransitions.Matches(actual, safeMap) || Hosting.ParseServerIdentity("steamid : " + actual.Identity) == null) continue;
            SearchBox.Clear(); FilterBox.SelectedIndex = 3; MapList.SelectedItem = safeMap;
            // Show the actual recovery configuration, so the next restart cannot repeat the failing map.
            LoadSettings(settings); ModeBox.SelectedIndex = settings.ModeIndex;
            transitionUnverified = false; serverExitedDuringLaunch = false;
            var notice = failedTitle + " 加载崩溃，已恢复为 Dust II。原工坊地图未修复；房间身份已重新获取，请复制新加入命令。";
            DisplayRoom(actual, notice); Save();
            RunText.Text = notice + "\n诊断已保存：" + logPath;
            Status("服务器已恢复 · 当前为 Dust II · 原地图加载失败");
            return;
        }
        throw new InvalidOperationException("恢复服务器未在 45 秒内就绪，已停止自动尝试。诊断：" + logPath);
    }
    // The terminal can raise the ceiling without making the operator hunt for the
    // spinbox on another window; the number is still only a launch argument, so it
    // cannot take effect without a restart.
    async Task RaiseCapacity(int cap)
    {
        if (launching) throw new InvalidOperationException("已有开图或重启操作正在执行。");
        var current = Settings().MaxPlayers;
        if (cap <= current) throw new InvalidOperationException($"当前上限已是 {current}，无需重启。");
        if (!smoke && MessageBox.Show(this, $"人数上限将从 {current} 提高到 {cap}。\n\n这是服务器进程的启动参数，必须重启独立服务器才能生效；当前对局会结束，所有玩家需要重新加入。\n\n游戏客户端不会关闭。", "提高人数上限并重启", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            throw new InvalidOperationException("已取消，未修改任何设置。");
        PlayersBox.Text = cap.ToString();
        Save();
        await RestartDedicatedServer();
        if (terminal != null && !terminal.IsClosed) terminal.UpdateCapacity(cap);
    }
    async void RestartServerClick(object sender, RoutedEventArgs e)
    {
        if (launching) { Status("正在开图或重启，请等待本次操作结束。"); return; }
        if (MessageBox.Show(this, "重启独立服务器会结束当前对局，所有玩家需要重新加入。\n\n先尝试正常停服；15 秒内未退出时，仅强制结束已核实的独立服务器。随后按当前设置加载所选地图。\n\n游戏客户端不会关闭。匿名 Steam 身份可能变化，请重新复制加入命令。", "重启独立服务器", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await RestartDedicatedServer();
    }

    async Task RestartDedicatedServer()
    {
        if (launching) throw new InvalidOperationException("已有开图或重启操作正在执行。");
        bool stopped = false;
        try
        {
            var settings = Settings(); var plan = Plan(); var root = RootBox.Text.Trim();
            if (settings.Kind == HostKind.Listen) throw new InvalidOperationException("此功能只重启独立服务器，请先选择独立开服模式。");
            if (settings.Kind == HostKind.DedicatedP2P && !RelayPatch.Enabled(root)) throw new InvalidOperationException("请先在“服务器”页启用 Steam P2P 配置。");
            Hosting.WriteConfigs(root, plan); // Validate destination before stopping anything.
            launching = true; IsEnabled = false;
            var endpoint = await Task.Run(() => GameProcesses.Find(root, true));
            roomGeneration++; transitionUnverified = true; observedRoom = null; roomNotice = null;
            InvalidateRoom("服务器正在重启，旧加入信息已失效…");
            if (endpoint != null)
            {
                using var process = Process.GetProcessById(endpoint.ProcessId);
                var started = process.StartTime;
                RunText.Text = "正在正常停止独立服务器，最多等待 15 秒…";
                using var control = new GameConsole();
                try
                {
                    if (endpoint.CommandPipe != null) { await control.Connect(endpoint.CommandPipe); await control.Send("quit"); }
                    else process.CloseMainWindow();
                }
                catch (Exception ex) { Status("正常停服请求未完成：" + ex.Message); }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException)
                {
                    // Re-identify the dedicated executable and its start time before termination.
                    var current = await Task.Run(() => GameProcesses.Find(root, true));
                    if (current?.ProcessId != endpoint.ProcessId || process.StartTime != started)
                        throw new InvalidOperationException("服务器进程已变化，取消强制结束，请重新检查。");
                    RunText.Text = "独立服务器未响应，正在结束已核实的服务器进程…";
                    process.Kill(); // Never kill a process tree or the player's client.
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            terminal?.ResetConnection();
            if (await Task.Run(() => GameProcesses.Find(root, true)) != null) throw new InvalidOperationException("服务器尚未退出，未重复启动。");
            // The process is gone; drop its pipes so the replacement gets fresh ones.
            localServer?.Dispose(); localServer = null;
            stopped = true;
        }
        catch (Exception ex) { RunText.Text = "重启未完成：" + ex.Message; Error(ex); }
        finally { launching = false; IsEnabled = true; }
        if (stopped)
        {
            transitionUnverified = false;
            await LaunchSelected();
        }
    }
}
