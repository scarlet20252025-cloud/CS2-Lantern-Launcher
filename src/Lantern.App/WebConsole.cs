using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Lantern.Core;

namespace Lantern.App;

/// <summary>
/// The LAN console: a small web page the host can share so other people can see
/// the room and drive it without installing anything.
///
/// What it deliberately cannot do: start or stop the server. Stopping the
/// server ends everyone's match, and only the person sitting at the machine
/// should be able to do that. Everything here is either read-only or a change
/// the host could have made from the terminal window anyway.
///
/// Every request carries a token the launcher prints, so a forwarded port is
/// not an open console.
/// </summary>
public partial class MainWindow
{
    const int DefaultWebPort = 8787;
    MiniHttp? web;
    int webBusy;
    // A smoke run must not move the operator's saved port; it overrides for this
    // process only. Writing prefs.WebPort from a test is how the port drifted.
    int? smokeWebPort;

    // One log ring shared by every viewer: draining it per request would mean
    // the second person to open the page sees nothing the first one already read.
    readonly object webLogLock = new();
    readonly List<(long Seq, string Text)> webLog = new();
    long webSeq;

    // Reading the room costs two console round-trips. Several viewers polling
    // every three seconds must not multiply that, so the result is reused briefly.
    (DateTime At, RoomStatus? Room, List<PlayerRow> Players, int? Capacity) snapshot;

    void StartWebConsole()
    {
        StopWebConsole();
        int port = smokeWebPort ?? (prefs.WebPort is >= 1024 and <= 65535 ? prefs.WebPort : DefaultWebPort);
        try
        {
            web = new MiniHttp(port, Handle);
            web.Start();
            WebState.Text = "网页控制台已启动（不需要先开服务器）。本机打开：http://127.0.0.1:" + port + "/"
                + (LanAddresses().FirstOrDefault() is string lan
                    ? "；好友打开：http://" + lan + ":" + port + "/" + (ProxyWarning("http://" + lan + ":" + port + "/") is string w ? "\n" + w : "")
                    : "；暂未找到内网地址。");
        }
        catch (Exception ex)
        {
            web = null;
            WebState.Text = $"网页控制台未启动：{ex.Message}（端口 {port} 可能已被占用；换一个端口再试。）";
        }
        UpdateWebAddresses();
    }

    void StopWebConsole()
    {
        web?.Dispose();
        web = null;
    }

    // Loopback first: it is the one URL that always works on this machine and is
    // exempt from every proxy configuration. The LAN addresses are for sharing.
    static IEnumerable<string> WebUrls(int port) =>
        new[] { $"http://127.0.0.1:{port}/" }.Concat(LanAddresses().Select(a => $"http://{a}:{port}/"));

    // Friends reach this through a LAN or forwarded address. A system proxy that
    // does not exempt that address answers 502 before the request ever gets here,
    // which looks exactly like the console being down. Say so instead.
    static string? ProxyWarning(string url)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) != 1) return null;
            var server = Convert.ToString(key?.GetValue("ProxyServer") ?? "");
            if (server.Length == 0) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            var host = uri.Host;
            var bypass = Convert.ToString(key?.GetValue("ProxyOverride") ?? "");
            foreach (var pattern in bypass.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var rule = pattern.Trim();
                if (rule.Equals("<local>", StringComparison.OrdinalIgnoreCase) && !host.Contains('.')) return null;
                if (rule is "localhost" && host is "localhost" or "127.0.0.1") return null;
                if (rule.EndsWith("*", StringComparison.Ordinal) && host.StartsWith(rule[..^1], StringComparison.OrdinalIgnoreCase)) return null;
                if (rule.Equals(host, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return $"浏览器走系统代理 {server}，而 {host} 不在代理的忽略列表里，打开会得到 502（页面看起来像挂了）。把 {host} 加进代理绕过列表，或让好友关闭代理。";
        }
        catch { return null; }
    }

    static IEnumerable<string> LanAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()))
            .Where(a => !a.StartsWith("127.", StringComparison.Ordinal) && !a.StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct()
            .ToList();

    void JoinAddressChanged(object sender, RoutedEventArgs e)
    {
        prefs.JoinAddress = JoinAddressBox.Text.Trim();
        Save();
        Status("加入地址已保存：" + (prefs.JoinAddress.Length > 0 ? prefs.JoinAddress : "未填（网页改用局域网地址）"));
    }

    void UpdateWebAddresses()
    {
        var port = web?.Port ?? smokeWebPort ?? (prefs.WebPort is >= 1024 and <= 65535 ? prefs.WebPort : DefaultWebPort);
        var urls = LanAddresses().Select(a => $"http://{a}:{port}/").ToList();
        WebAddressBox.ItemsSource = urls;
        WebAddressBox.SelectedIndex = urls.Count > 0 ? 0 : -1;
        if (urls.Count == 0) WebState.Text = "没有找到可用的内网地址（网络未连接？）。本机仍可用 http://127.0.0.1:" + port + "/ 打开。";
    }

    void ShowWebConsole(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedIndex = 2;
        WebSection.IsExpanded = true;
        WebPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => WebPanel.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        if (WebCheck.IsChecked != true)
        {
            WebCheck.IsChecked = true;
            WebCheckClick(WebCheck, e);
        }
        else if (web == null) StartWebConsole();
        Status("网页控制台：" + (web == null ? WebState.Text : string.Join("  ", WebUrls(web.Port))));
    }

    void OpenWebClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Local verification always uses loopback: it needs no proxy exemption
            // and works even when the machine has no LAN address yet.
            var url = web == null ? "" : $"http://127.0.0.1:{web.Port}/";
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("网页控制台未启动。");
            OpenUri(url);
            Status("已在浏览器打开 " + url + "。这个页面不需要口令；发给好友时用“复制地址”。");
        }
        catch (Exception ex) { Error(ex); }
    }

    void CopyWebAddress(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = WebAddressBox.SelectedItem as string ?? WebAddressBox.Text;
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("还没有可用地址。");
            if (url.Contains("127.0.0.1") || url.Contains("localhost"))
                throw new InvalidOperationException("127.0.0.1 只有你自己能打开。请在下拉框里选一个局域网地址再复制。");
            Clipboard.SetText(url);
            Status("已复制网页地址，发给好友即可。公网访问需要你自己把该端口转发出去。"
                + (ProxyWarning(url) is string warn ? " 注意：" + warn : ""));
        }
        catch (Exception ex) { Error(ex); }
    }

    void WebCheckClick(object sender, RoutedEventArgs e)
    {
        prefs.WebEnabled = WebCheck.IsChecked == true;
        Save();
        if (prefs.WebEnabled) StartWebConsole(); else StopWebConsole();
        WebCheck.IsChecked = prefs.WebEnabled;
        WebPanel.Visibility = prefs.WebEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    void WebPortChanged(object sender, RoutedEventArgs e)
    {
        if (WebPortBox == null) return;
        if (!int.TryParse(WebPortBox.Text, out var port) || port is < 1024 or > 65535)
        {
            WebState.Text = "网页端口需为 1024–65535 的整数。";
            return;
        }
        prefs.WebPort = port; Save();
        if (prefs.WebEnabled) StartWebConsole();
    }

    /// <summary>
    /// Runs work on the UI thread. The room state, the terminal and every
    /// control live there; a request thread touching them directly is how WPF
    /// apps get "the calling thread cannot access this object".
    /// </summary>
    Task<T> OnUi<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(async () =>
        {
            try { done.TrySetResult(await work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }

    async Task<HttpResponse> Handle(HttpRequest request)
    {
        try
        {
            if (request.Method == "GET" && request.Path is "/" or "/index.html") return HttpResponse.Html(WebPage.Html);
            if (request.Method == "GET" && request.Path == "/favicon.ico") return HttpResponse.Text("", 404);
            if (!request.Path.StartsWith("/api/", StringComparison.Ordinal)) return HttpResponse.Text("没有这个地址。", 404);

            // No password: the host asked for a console anyone on the forwarded
            // address can just open. There is nothing to unlock, so nothing here
            // pretends to be a gate.

            // Images are served outside the JSON switch: they answer with bytes.
            if (request.Path == "/api/cover" && request.Method == "GET") return Cover(request.Param("identity") ?? "");

            return (request.Path, request.Method) switch
            {
                ("/api/state", "GET") => HttpResponse.Json(await OnUi(() => StateJsonAsync(request))),
                ("/api/map", "POST") => HttpResponse.Json(await OnUi(() => SwitchMapFromWeb(request))),
                ("/api/command", "POST") => HttpResponse.Json(await OnUi(() => CommandFromWeb(request))),
                ("/api/bots", "POST") => HttpResponse.Json(await OnUi(() => BotsFromWeb(request))),
                ("/api/quick", "POST") => HttpResponse.Json(await OnUi(() => QuickFromWeb(request))),
                ("/api/server", "POST") => HttpResponse.Json(await OnUi(() => ServerFromWeb(request))),
                _ => HttpResponse.Text("没有这个接口。", 404),
            };
        }
        catch (Exception ex)
        {
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    /// <summary>
    /// Re-reads the room at most every two seconds. Runs on the UI thread (the
    /// caller is already there) because the terminal, the map list and every
    /// control belong to it; it only awaits, so the window stays responsive.
    /// Never invents a state: a failed read returns its reason to the page.
    /// </summary>
    async Task<(RoomStatus? Room, List<PlayerRow> Players, int? Capacity, string? Error)> CurrentRoom(bool force)
    {
        if (!force && (DateTime.UtcNow - snapshot.At).TotalMilliseconds < 2000)
            return (snapshot.Room, snapshot.Players, snapshot.Capacity, null);
        string? error = null;
        try
        {
            var root = RootBox.Text.Trim();
            // WMI costs tens of milliseconds; never on the UI thread.
            var found = await Task.Run(() => GameProcesses.Find(root, true));
            var capacity = found?.RequestedMaxPlayers;
            if (HostBox.SelectedIndex == 0 || root.Length == 0)
            {
                snapshot = (DateTime.UtcNow, null, new(), null);
                return (null, new(), null, null);
            }
            // Nobody may have opened the terminal on this machine; the web console
            // still needs one. Terminal() builds it without showing it.
            var window = Terminal();
            if (!window.IsConnected)
            {
                if (found == null)
                {
                    snapshot = (DateTime.UtcNow, null, new(), capacity);
                    return (null, new(), capacity, null);
                }
            }
            var room = await window.GetRoomStatus();
            var players = await window.GetPlayers();
            snapshot = (DateTime.UtcNow, room, players, capacity);
            lastRoom = room;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            snapshot = (DateTime.UtcNow, snapshot.Room, snapshot.Players, snapshot.Capacity);
        }
        return (snapshot.Room, snapshot.Players, snapshot.Capacity, error);
    }

    async Task<string> StateJsonAsync(HttpRequest request)
    {
        long since = long.TryParse(request.Param("since"), out var parsed) ? parsed : webSeq;
        // A read can fail while the server is between processes (right after a
        // start or a restart). That is a state, not an API error: answering with
        // {ok:false} made the page show a failure toast for a normal boot.
        RoomStatus? room = null; List<PlayerRow> players = new(); int? capacity = null; string? error = null;
        try { (room, players, capacity, error) = await CurrentRoom(false); }
        catch (Exception ex) { error = ex.Message; }
        if (room != null) lastRoom = room;

        List<string> lines;
        lock (webLogLock) lines = webLog.Where(e => e.Seq > since).Select(e => e.Text).ToList();

        var payload = new
        {
            ok = true,
            busy = launching || webBusy > 0,
            // Launching narrates itself into RunText ("正在初始化…", "正在加载工坊地图…",
            // "已就绪"). Forwarding that is a real stage, not a timer pretending to be one.
            stage = launching ? RunText.Text : "",
            error,
            server = new
            {
                running = room != null,
                map = room?.Map ?? "",
                addon = room?.Addon ?? "",
                identity = room?.Identity ?? "",
                port = room?.Port ?? 0,
                humans = room?.Humans ?? 0,
                bots = room?.Bots ?? 0,
                capacity,
                listen = HostBox.SelectedIndex == 0,
            },
            players = players.Select(p => new
            {
                id = p.Id,
                name = p.Name.Length == 0 ? "(空槽)" : p.Name,
                state = p.State,
                bot = p.IsBot,
                ready = p.IsReady,
            }).ToList(),
            maps = maps.Where(m => m.Local).Select(m => new
            {
                identity = m.Identity,
                title = m.Title,
                mapName = m.MapName,
                official = m.Official,
                // The page hands this straight to Steam; the URL is built in Core
                // so it cannot drift from the launcher's own workshop button.
                workshopUrl = m.Official ? "" : Workshop.FilePageUrl(m.Id),
                cover = m.Official ? OfficialMaps.HasCover(m.MapName) : File.Exists(Path.Combine(dataDir, "covers", m.Id + ".jpg")),
                current = room != null && RoomTransitions.Matches(room, m),
            }).ToList(),
            join = JoinPayload(room),
            seq = webSeq,
            log = lines,
        };
        return JsonSerializer.Serialize(payload);
    }

    // The one command a friend runs: Win+R, paste, Enter. Prefer the address the
    // host configured, because that is the one they actually forwarded; never
    // invent a public address, and say which source it came from.
    object JoinPayload(RoomStatus? room)
    {
        if (room == null) return new { link = "", command = "", source = "", error = "" };
        // The saved share address wins: it is the one the host actually forwarded,
        // and it may carry its own port (frp maps UDP 10003 -> 27016). Falling
        // back to the game port silently produced a command nobody could use.
        var saved = (prefs.JoinAddress ?? "").Trim();
        var configured = saved.Length > 0 ? saved : AddressBox.Text.Trim();
        if (configured.Length == 0)
            return new { link = "", command = "", source = "", error = "" };
        var host = configured;
        int? explicitPort = null;
        var colon = configured.LastIndexOf(':');
        if (colon > 0 && int.TryParse(configured[(colon + 1)..], out var parsed) && parsed is >= 1 and <= 65535)
        { host = configured[..colon]; explicitPort = parsed; }
        try
        {
            var url = Hosting.SteamConnectUrl(host, explicitPort ?? room.Port);
            return new
            {
                link = url,
                command = url,
                source = saved.Length > 0 ? "启动器里固定的加入地址" : "「好友联机」页填写的地址",
                error = "",
            };
        }
        catch (ArgumentException ex)
        {
            return new { link = "", command = "", source = "", error = ex.Message };
        }
    }

    async Task<string> SwitchMapFromWeb(HttpRequest request)
    {
        string identity;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            identity = json.RootElement.TryGetProperty("identity", out var value) ? value.GetString() ?? "" : "";
        }
        catch (JsonException) { return Fail("请求格式不对。"); }
        try
        {
            if (HostBox.SelectedIndex == 0)
                throw new InvalidOperationException("房主用的是“本机边玩边开”，这个模式没有可换图的独立服务器。请房主改用独立服务器。");
            if (launching || webBusy > 0) throw new InvalidOperationException("服务器正在处理上一次操作，请稍后再试。");
            var map = maps.FirstOrDefault(m => m.Identity == identity) ?? throw new ArgumentException("没有这张地图。");
            if (!map.Local) throw new ArgumentException("这张地图还没下载完。");
            webBusy++;
            try
            {
                var root = RootBox.Text.Trim(); // read on the UI thread, use in the worker
                var running = await Task.Run(() => GameProcesses.Find(root, true));
                if (running == null)
                {
                    // Nothing is running yet, so "switch to this map" means "start
                    // on this map" — the same slow path, answered the same way.
                    InBackground("启动服务器", () => StartServerOn(map));
                    return JsonSerializer.Serialize(new { ok = true, map = map.Title, started = true, detail = "已开始启动并加载这张地图。" });
                }
                await SwitchExistingRoom(map, Settings(), RootBox.Text.Trim());
                snapshot = default;
                return JsonSerializer.Serialize(new { ok = true, map = map.Title });
            }
            finally { webBusy--; }
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    // The launcher's launch path reads a target, not an identity, so the page
    // hands it one directly. Selecting it in the list as well keeps the two
    // views agreeing about what is pending.
    async Task StartServerOn(MapEntry map)
    {
        var listed = (MapList.ItemsSource as IEnumerable<MapEntry>)?.FirstOrDefault(m => m.Identity == map.Identity);
        if (listed != null) MapList.SelectedItem = listed;
        launchTarget = map;
        try { await LaunchSelected(); }
        finally { launchTarget = null; }
        snapshot = default;
    }

    /// <summary>
    /// Runs a slow launcher action after the HTTP response has gone out. Both
    /// starting and restarting verify the map, which can take a minute; MiniHttp
    /// closes a connection after 20 s, so awaiting here reported a transport
    /// error for work that was actually progressing.
    /// </summary>
    void InBackground(string label, Func<Task> work)
    {
        webBusy++;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try { await work(); WebLog($"[系统] {label}完成。"); }
            catch (Exception ex) { WebLog($"[系统] {label}未完成：" + ex.Message); }
            finally { webBusy--; snapshot = default; }
        });
    }

    async Task<string> ServerFromWeb(HttpRequest request)
    {
        string action;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            action = json.RootElement.TryGetProperty("action", out var value) ? value.GetString() ?? "" : "";
        }
        catch (JsonException) { return Fail("请求格式不对。"); }
        try
        {
            if (HostBox.SelectedIndex == 0)
                throw new InvalidOperationException("房主用的是“本机边玩边开”，网页无法启动这种模式。请房主改用独立服务器。");
            if (launching || webBusy > 0) throw new InvalidOperationException("服务器正在处理上一次操作，请稍后再试。");
            var root = RootBox.Text.Trim();
            var running = await Task.Run(() => GameProcesses.Find(root, true)) != null;
            switch (action)
            {
                case "start":
                    if (running) return Fail("服务器已经在运行。要换图请直接点地图卡片。");
                    var target = Selected();
                    InBackground("启动服务器", () => StartServerOn(target));
                    return JsonSerializer.Serialize(new { ok = true, detail = "已开始启动服务器，正在核验地图；状态会自己刷新。" });
                case "restart":
                    if (!running) return Fail("服务器没在运行，直接点地图卡片启动即可。");
                    // Restart puts back the map people are already on. Launching
                    // whatever the launcher's list happens to have selected would
                    // silently move everyone to a different map.
                    var live = lastRoom;
                    var same = live == null ? null
                        : maps.FirstOrDefault(m => m.MapName == live.Map && (m.Official || m.Id == live.Addon))
                          ?? maps.FirstOrDefault(m => m.MapName == live.Map);
                    InBackground("重启服务器", async () =>
                    {
                        launchTarget = same;
                        try { await RestartDedicatedServer(); }
                        finally { launchTarget = null; }
                    });
                    return JsonSerializer.Serialize(new { ok = true, detail = "已开始重启服务器，正在核验地图。" });
                default:
                    return Fail("不认识的操作。");
            }
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    async Task<string> CommandFromWeb(HttpRequest request)
    {
        string command;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            command = json.RootElement.TryGetProperty("command", out var value) ? (value.GetString() ?? "").Trim() : "";
        }
        catch (JsonException) { return Fail("请求格式不对。"); }
        if (command.Length == 0) return Fail("命令是空的。");
        try
        {
            if (launching) throw new InvalidOperationException("正在换图，等地图核验完成再执行命令。");
            await Terminal().SendCommand(command);
            return JsonSerializer.Serialize(new { ok = true });
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    async Task<string> BotsFromWeb(HttpRequest request)
    {
        string action = "", team = "";
        int count = 0;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            team = root.TryGetProperty("team", out var t) ? t.GetString() ?? "" : "";
            if (root.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number) count = c.GetInt32();
        }
        catch (JsonException) { return Fail("请求格式不对。"); }
        try
        {
            if (launching || webBusy > 0) throw new InvalidOperationException("服务器正在处理上一次操作，请稍后再试。");
            webBusy++;
            try
            {
                var console = Terminal();
                await console.ConnectGame();
                var connection = console.Connection ?? throw new InvalidOperationException("终端未连接。");
                switch (action)
                {
                    case "add":
                        if (team is not ("t" or "ct")) throw new ArgumentException("队伍只能是 t 或 ct。");
                        if (count is < 1 or > 32) throw new ArgumentException("人数需为 1–32。");
                        var added = await TeamBots.Add(connection, team, count, snapshot.Capacity ?? 64);
                        return JsonSerializer.Serialize(new { ok = true, detail = $"已加入 {added.Added} 个人机。" + (added.Detail.Length > 0 ? " " + added.Detail : "") });
                    case "remove":
                        await console.SendCommand(QuickCommands.RemoveBots);
                        return JsonSerializer.Serialize(new { ok = true, detail = "已发送清除人机命令。" });
                    case "balance":
                        if (count is < 2 or > 64) throw new ArgumentException("总人数需为 2–64。");
                        await console.SendCommand(QuickCommands.Balance(count));
                        return JsonSerializer.Serialize(new { ok = true, detail = "已发送补位命令，人数包含真人。" });
                    case "restart":
                        await console.SendCommand(QuickCommands.Restart);
                        return JsonSerializer.Serialize(new { ok = true, detail = "已发送重开对局命令。" });
                    default: throw new ArgumentException("不认识的操作。");
                }
            }
            finally { webBusy--; }
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    // A closed list, not a command passthrough: whoever holds the token can already
    // type into /api/command, so this exists to be obvious, not to be powerful.
    async Task<string> QuickFromWeb(HttpRequest request)
    {
        string action;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            action = json.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        }
        catch (JsonException) { return Fail("请求格式不对。"); }
        // The list lives in Core so it can be tested without a server.
        var command = QuickCommands.ForAction(action);
        if (command == null) return Fail("不认识的操作。");
        try
        {
            if (launching || webBusy > 0) throw new InvalidOperationException("服务器正在处理上一次操作，请稍后再试。");
            webBusy++;
            try
            {
                var console = Terminal();
                await console.ConnectGame();
                await console.SendCommand(command);
                return JsonSerializer.Serialize(new { ok = true, detail = "已下发：" + command });
            }
            finally { webBusy--; }
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    // Covers come from two very different places: official maps ship a PNG inside
    // the executable, workshop maps have a JPEG cached from Steam. Both are read
    // once and kept, keyed by identity, because the page asks on every render.
    readonly Dictionary<string, (string Stamp, byte[] Bytes, string Type)> covers = new();
    readonly Queue<string> coverOrder = new();

    HttpResponse Cover(string identity)
    {
        try
        {
            var map = maps.FirstOrDefault(m => m.Identity == identity);
            if (map == null) return HttpResponse.Text("没有这张地图。", 404);
            var stamp = map.Official
                ? "official:" + map.MapName
                : CoverPath(map) is { } path && File.Exists(path) ? path + "|" + File.GetLastWriteTimeUtc(path).Ticks : "";
            if (stamp.Length == 0) return HttpResponse.Text("这张地图没有封面。", 404);
            if (covers.TryGetValue(stamp, out var cached))
                return HttpResponse.Bytes(cached.Bytes, cached.Type).With("Cache-Control", "private, max-age=900");
            var bytes = map.Official ? EmbeddedCover(map.MapName) : File.ReadAllBytes(CoverPath(map)!);
            if (bytes == null) return HttpResponse.Text("这张地图没有封面。", 404);
            var type = map.Official ? "image/png" : "image/jpeg";
            while (coverOrder.Count >= 64) covers.Remove(coverOrder.Dequeue());
            covers[stamp] = (stamp, bytes, type);
            coverOrder.Enqueue(stamp);
            return HttpResponse.Bytes(bytes, type).With("Cache-Control", "private, max-age=900");
        }
        catch (Exception ex) { return HttpResponse.Text("封面读取失败：" + ex.Message, 500); }
    }

    string? CoverPath(MapEntry map) => map.Official ? null : Path.Combine(dataDir, "covers", map.Id + ".jpg");

    static byte[]? EmbeddedCover(string mapName)
    {
        if (!OfficialMaps.HasCover(mapName)) return null;
        var resource = System.Windows.Application.GetResourceStream(
            new Uri($"pack://application:,,,/Assets/Maps/{mapName}.png"));
        if (resource == null) return null;
        using var stream = resource.Stream;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static string Fail(string message) => JsonSerializer.Serialize(new { ok = false, error = message });

    /// <summary>Called from the terminal's log pump; must stay cheap and thread-safe.</summary>
    void WebLog(string text)
    {
        var seq = Interlocked.Increment(ref webSeq);
        lock (webLogLock)
        {
            webLog.Add((seq, text.TrimEnd('\r', '\n')));
            if (webLog.Count > 800) webLog.RemoveRange(0, webLog.Count - 800);
        }
    }
}
