using System.Windows;
using Lantern.Core;

namespace Lantern.App;

public partial class MainWindow
{
    bool readingRoom;
    RoomStatus? observedRoom;
    string? roomNotice;
    int roomGeneration;
    bool transitionUnverified;
    readonly System.Windows.Threading.DispatcherTimer roomTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    void InvalidateRoom(string message)
    {
        lastRoom = null;
        if (AddressBox.Text.StartsWith("[A:")) AddressBox.Clear();
        RoomInfo.Text = message;
    }

    void DisplayRoom(RoomStatus room, string? transition = null)
    {
        var old = observedRoom;
        bool changed = old != null && (old.Identity != room.Identity || old.Port != room.Port);
        lastRoom = room; observedRoom = room;
        if (transition != null) roomNotice = transition;
        else if (changed) roomNotice = "房间身份或端口已变化，旧 Steam 命令已过期，请重新复制。";
        PortBox.Text = room.Port.ToString();
        var identity = Hosting.ParseServerIdentity("steamid : " + room.Identity);
        // Preserve an explicitly chosen VPN/public IP. Steam identities are per-room.
        if (AddressBox.Text.Length == 0 || AddressBox.Text.StartsWith("[A:"))
        {
            AddressBox.Text = HostBox.SelectedIndex != 1 && identity != null ? identity : "";
            if (AddressBox.Text.Length == 0)
            {
                LoadAddresses();
                if (NetworkAddresses.ItemsSource is List<NetworkAddress> addresses)
                    NetworkAddresses.SelectedItem = addresses.FirstOrDefault(a => NetworkPriority(a) > 0);
            }
        }
        RoomInfo.Text = $"当前地图：{room.Map}\n真人 {room.Humans} · 人机 {room.Bots} · UDP {room.Port}\n" +
            (roomNotice ?? "已同步当前房间；复制前会再次核验。") +
            "\n好友网络连通情况仍需实际验证。";
    }

    async Task MonitorRoom()
    {
        if (launching || readingRoom || transitionUnverified || terminal == null) return;
        if (!terminal.IsConnected)
        {
            if (HostBox.SelectedIndex != 0)
            {
                var root = RootBox.Text.Trim();
                try { if (await Task.Run(() => GameProcesses.Find(root, true)) == null) ReportServerExited(); }
                catch { /* A process inspection error must not be reported as a crash. */ }
            }
            return;
        }
        readingRoom = true; int generation = roomGeneration;
        try
        {
            var room = await terminal.GetRoomStatus();
            if (generation == roomGeneration && !launching) DisplayRoom(room);
        }
        catch (Exception ex) { if (generation == roomGeneration && !launching) InvalidateRoom("房间暂未就绪：" + ex.Message); }
        finally { readingRoom = false; }
    }

    async Task<(RoomStatus Room, string Address)> VerifiedTarget(bool allowLoopback = false)
    {
        if (launching) throw new InvalidOperationException("正在换图，请等待地图与房间核验完成后再复制。");
        if (transitionUnverified) throw new InvalidOperationException("上次换图尚未核实。请先点击“读取当前房间与加入地址”，确认实际地图后再分享。");
        int generation = roomGeneration;
        var room = await Terminal().GetRoomStatus();
        if (generation != roomGeneration || launching) throw new InvalidOperationException("房间正在变化，请稍后重试。");
        DisplayRoom(room);
        if (!allowLoopback && AddressBox.Text.Trim() == "127.0.0.1") throw new ArgumentException("127.0.0.1 不能分享给好友，请选择组网或公网地址。");
        return (room, AddressBox.Text.Trim());
    }

    async Task<string> FreshJoin(bool reconnect)
    {
        var (room, address) = await VerifiedTarget();
        return reconnect ? RoomTransitions.ReconnectCommand(address, room.Port)
            : Hosting.JoinCommand(address, room.Port);
    }

    // The friend clicks this link instead of opening the console: Steam wakes the
    // client and connects. Verified against a live launcher-owned server.
    async Task<string> FreshLink(bool allowLoopback = false)
    {
        var (room, address) = await VerifiedTarget(allowLoopback);
        return Hosting.SteamConnectUrl(address, room.Port);
    }

    async void CopyLink(object sender, RoutedEventArgs e)
    {
        try
        {
            var link = await FreshLink();
            Clipboard.SetText(link);
            Status("已复制加入指令：" + link + "。对方按 Win+R 粘贴回车即可进服，不必装启动器、也不必打开控制台。");
        }
        catch (Exception ex) { Error(ex); }
    }

    async void CopyReconnect(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(await FreshJoin(true)); Status("已复制好友重连命令：好友会先退出旧连接，再加入当前房间；房主不受影响。"); }
        catch (Exception ex) { Error(ex); }
    }

    async void JoinDedicatedClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (HostBox.SelectedIndex == 0) throw new InvalidOperationException("此按钮用于独立服务器；本机边玩边开模式已经在房间内。");
            if (launching || transitionUnverified) throw new InvalidOperationException("请先等待地图核验完成，或读取当前房间确认状态。");
            int generation = roomGeneration;
            var room = await Terminal().GetRoomStatus();
            var selected = Selected();
            if (!RoomTransitions.Matches(room, selected))
            {
                DisplayRoom(room);
                throw new InvalidOperationException($"服务器实际仍是 {room.Map}，不是所选的 {selected.Title}。未让客户端加入旧地图。请先点“启动 / 切换服务器地图”，等核实完成再加入。");
            }
            // The client has no console any more (-netconport is gone and a
            // Steam-launched process gives us no stdout), so we cannot push a
            // connect command into it. A Steam link needs no console at all.
            if (generation != roomGeneration || launching) throw new InvalidOperationException("房间已变化，请稍后重试。");
            DisplayRoom(room);
            var address = HostBox.SelectedIndex == 2 ? room.Identity : "127.0.0.1";
            if (address.StartsWith("[A:", StringComparison.Ordinal))
            {
                var command = Hosting.JoinCommand(address, room.Port);
                Clipboard.SetText(command);
                Status("P2P 身份无法生成邀请链接，已复制加入命令：" + command + "。请在游戏控制台粘贴执行；服务器保持运行。");
                return;
            }
            var link = Hosting.SteamConnectUrl(address, room.Port);
            int before = room.Humans;
            OpenUri(link);
            Status("已请求 Steam 加入本机服务器，正在确认是否真的连入…");
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1500);
                RoomStatus now;
                try { now = await Terminal().GetRoomStatus(); }
                catch { continue; }
                if (now.Humans > before)
                {
                    DisplayRoom(now, "本机客户端已连入服务器。");
                    Status("本机客户端已连入服务器（真人 " + now.Humans + "）。");
                    return;
                }
            }
            Status("已打开 " + link + "，但 45 秒内没有检测到本机玩家进入。若 Steam 没有唤起游戏，请确认它已登录，或手动复制该链接。");
        }
        catch (Exception ex) { Error(ex); }
    }

    async Task SwitchExistingRoom(MapEntry expected, HostSettings settings, string root)
    {
        var serverEndpoint = settings.Kind == HostKind.Listen ? null : await Task.Run(() => GameProcesses.Find(root, true));
        using var serverProcess = serverEndpoint == null ? null : System.Diagnostics.Process.GetProcessById(serverEndpoint.ProcessId);
        if (!expected.Official && serverProcess != null && !string.Equals(serverProcess.MainModule?.FileName,
            DedicatedRuntime.Executable(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前服务器仍使用旧运行目录，可能误校验工坊资源。请点“重启独立服务器”迁移到兼容目录后再开图；游戏客户端不会关闭。");
        var target = Terminal();
        await target.ConnectGame(); // Authentication errors must never trigger a blind second launch.
        RoomStatus? before = null;
        try { before = await target.GetRoomStatus(); }
        catch (InvalidOperationException) { /* Menu / inactive server: initial map load is required. */ }
        if (settings.Kind != HostKind.Listen && before?.Humans > 0)
        {
            int active = await target.GetActiveHumans();
            if (active != before.Humans)
                throw new InvalidOperationException($"玩家仍在连接或加载（已就绪 {active}/{before.Humans}），未发送换图命令。请等玩家进入地图后再换图，避免客户端重连地址丢失。");
        }
        var session = "switch_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var plan = before == null ? Hosting.Build(expected, settings, session)
            : RoomTransitions.ForExistingRoom(expected, settings, session);
        var exec = Hosting.WriteConfigs(root, plan); lastRules = RoomTransitions.ConsoleCommands(plan.Rules);
        roomGeneration++; transitionUnverified = true;
        InvalidateRoom("正在切换地图，旧加入信息暂停使用…");
        RunText.Text = before == null ? "正在建立房间…" : "正在保留房间切换地图，等待核验…";
        var activation = target.MapActivation;
        var spawnBefore = await target.GetSpawnCount();
        // Workshop cfg execution rejects changelevel. Send validated commands directly through NetCon.
        await target.SendCommand(RoomTransitions.ConsoleCommands(plan.Boot)); // Never replay a mutation.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        string lastError = "等待所选地图加载";
        while (DateTime.UtcNow < deadline && !lifetime.IsCancellationRequested)
        {
            // Activation output wakes this immediately; the short fallback poll
            // also handles reconnects where an activation line was missed.
            await target.WaitForMapActivity(200, lifetime.Token);
            if (serverProcess?.HasExited == true)
            {
                ReportServerExited();
                throw new InvalidOperationException(ServerExitedMessage);
            }
            try
            {
                var actual = await target.GetRoomStatus();
                bool matches = RoomTransitions.Matches(actual, expected);
                bool activated = target.MapActivation > activation;
                if (matches && !activated && spawnBefore.HasValue)
                    activated = await target.GetSpawnCount() is int spawnAfter && spawnAfter > spawnBefore.Value;
                matches &= activated;
                if (matches)
                {
                    await target.SendCommand(RoomTransitions.ConsoleCommands(plan.Rules));
                    transitionUnverified = false;
                    var message = before == null ? "新房间已建立，请复制当次加入命令。" : RoomTransitions.SameEndpoint(before, actual)
                        ? "房间身份和端口已保留，可继续使用原地址；卡住的好友可用下方重连命令。"
                        : "地图已切换，但房间身份或端口发生变化；旧 Steam 命令已过期，请重新复制。";
                    DisplayRoom(actual, message); Save();
                    RunText.Text = "已核实所选地图：" + expected.Title + "。" + message +
                        (settings.Kind != HostKind.Listen ? "\n独立服务器已就绪。要进入游戏，点击“本机加入此服务器”。" : "");
                    Status(message); return;
                }
                lastError = $"当前 {actual.Map} / 工坊 {actual.Addon}，等待目标 {expected.MapName}";
            }
            catch (Exception ex) { lastError = ex.Message; }
        }
        throw new InvalidOperationException("换图尚未确认：" + lastError + "。未自动断开房间或重复换图；请查看终端，或读取当前房间核实。");
    }

    async Task TestRoomTransitions()
    {
        var target = Terminal();
        var first = await target.GetRoomStatus();
        var workshop = maps.First(m => m.Id == "3815559839" && m.MapName == "crazy_chickens" && m.Local);
        var official = maps.First(m => m.Official && m.MapName == "de_dust2");
        var evidence = new List<string>();
        foreach (var map in new[] { workshop, workshop, official })
        {
            launching = true;
            try { await SwitchExistingRoom(map, Settings(), RootBox.Text.Trim()); }
            finally { launching = false; }
            if (lastRoom == null || !RoomTransitions.Matches(lastRoom, map) || !RoomTransitions.SameEndpoint(first, lastRoom))
                throw new Exception("Real transition changed endpoint or loaded wrong map");
            evidence.Add($"PASS {lastRoom.Map} / addon {lastRoom.Addon}: same Steam identity and UDP port; activation + exact status verified");
        }
        AddressBox.Text = "[A:1:1:1]";
        var fresh = await FreshJoin(false);
        if (!fresh.Contains(first.Identity)) throw new Exception("Stale identity was shared");
        AddressBox.Text = "10.126.126.5";
        var reconnect = await FreshJoin(true);
        if (reconnect != "disconnect; connect 10.126.126.5:" + first.Port) throw new Exception("VPN selection not preserved");
        evidence.Add("PASS copy refresh replaces stale Steam identity; explicit VPN IP preserved; friend reconnect command verified");
        SettingsTabs.SelectedIndex = 1;
        System.IO.File.WriteAllLines(System.IO.Path.Combine(dataDir, "transition-smoke.txt"), evidence);
    }
}
