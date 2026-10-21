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
    string webToken = "";
    int webBusy;

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
        if (webToken.Length < 8)
        {
            webToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            prefs.WebToken = webToken;
            Save();
        }
        int port = prefs.WebPort is >= 1024 and <= 65535 ? prefs.WebPort : DefaultWebPort;
        try
        {
            web = new MiniHttp(port, Handle);
            web.Start();
            WebState.Text = "网页控制台已启动。同一局域网（或你转发的公网端口）打开：" + string.Join("   ", WebUrls(port));
        }
        catch (Exception ex)
        {
            web = null;
            WebState.Text = $"网页控制台未启动：{ex.Message}（端口 {port} 可能已被占用；换一个端口再试。）";
        }
        WebTokenText.Text = webToken;
        UpdateWebAddresses();
    }

    void StopWebConsole()
    {
        web?.Dispose();
        web = null;
    }

    static IEnumerable<string> WebUrls(int port) => LanAddresses().Select(a => $"http://{a}:{port}/");

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

    void UpdateWebAddresses()
    {
        var port = web?.Port ?? (prefs.WebPort is >= 1024 and <= 65535 ? prefs.WebPort : DefaultWebPort);
        var urls = WebUrls(port).ToList();
        WebAddressBox.ItemsSource = urls;
        WebAddressBox.SelectedIndex = urls.Count > 0 ? 0 : -1;
        if (urls.Count == 0) WebState.Text = "没有找到可用的内网地址（网络未连接？）。";
    }

    void ShowWebConsole(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedIndex = 2;
        WebPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => WebPanel.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        if (WebCheck.IsChecked != true)
        {
            WebCheck.IsChecked = true;
            WebCheckClick(WebCheck, e);
        }
        else if (web == null) StartWebConsole();
        Status("网页控制台：" + (web == null ? WebState.Text : string.Join("  ", WebUrls(web.Port)) + "  口令 " + webToken));
    }

    void OpenWebClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = WebAddressBox.SelectedItem as string ?? WebAddressBox.Text;
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("还没有可用地址。");
            if (web == null) throw new InvalidOperationException("网页控制台未启动，请先勾选“启用网页控制台”。");
            // The fragment carries the token: never sent to the server, never in a
            // request log, and the host does not have to retype it.
            OpenUri(url + "#token=" + webToken);
            Status("已在浏览器打开 " + url + "（口令随地址一起带过去，不用手输）。发给好友时请用“复制地址和口令”。");
        }
        catch (Exception ex) { Error(ex); }
    }

    void CopyWebAddress(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = WebAddressBox.SelectedItem as string ?? WebAddressBox.Text;
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("还没有可用地址。");
            Clipboard.SetText(url + "  口令：" + webToken);
            Status("已复制网页地址和口令，发给好友即可。公网访问需要你自己把该端口转发出去。");
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

            // Login is the only route reachable without a cookie.
            if (request.Path == "/api/login" && request.Method == "POST")
            {
                if (!SameSecret(ReadToken(request), webToken))
                    return HttpResponse.Json("{\"ok\":false,\"error\":\"口令不对。\"}");
                return HttpResponse.Json("{\"ok\":true}").With("Set-Cookie", "lantern=" + webToken + "; Path=/; Max-Age=86400; SameSite=Strict; HttpOnly");
            }

            if (!SameSecret(CookieToken(request), webToken))
                return HttpResponse.Json("{\"ok\":false,\"error\":\"需要口令\",\"auth\":false}");

            return (request.Path, request.Method) switch
            {
                ("/api/state", "GET") => HttpResponse.Json(await OnUi(() => StateJsonAsync(request))),
                ("/api/map", "POST") => HttpResponse.Json(await OnUi(() => SwitchMapFromWeb(request))),
                ("/api/command", "POST") => HttpResponse.Json(await OnUi(() => CommandFromWeb(request))),
                ("/api/bots", "POST") => HttpResponse.Json(await OnUi(() => BotsFromWeb(request))),
                _ => HttpResponse.Text("没有这个接口。", 404),
            };
        }
        catch (Exception ex)
        {
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    static bool SameSecret(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied) || expected.Length == 0) return false;
        var a = Encoding.UTF8.GetBytes(supplied);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    static string ReadToken(HttpRequest request)
    {
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            return json.RootElement.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String
                ? (token.GetString() ?? "").Trim() : "";
        }
        catch (JsonException) { return ""; }
    }

    static string? CookieToken(HttpRequest request)
    {
        var raw = request.Header("Cookie");
        if (string.IsNullOrEmpty(raw)) return null;
        foreach (var part in raw.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("lantern=", StringComparison.Ordinal)) return trimmed["lantern=".Length..];
        }
        return null;
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
        var (room, players, capacity, error) = await CurrentRoom(false);
        if (room != null) lastRoom = room;

        List<string> lines;
        lock (webLogLock) lines = webLog.Where(e => e.Seq > since).Select(e => e.Text).ToList();

        var payload = new
        {
            ok = true,
            busy = launching || webBusy > 0,
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
                current = room != null && RoomTransitions.Matches(room, m),
            }).ToList(),
            seq = webSeq,
            log = lines,
        };
        return JsonSerializer.Serialize(payload);
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
                throw new InvalidOperationException("房主用的是“本机边玩边开”，这个模式没有可换图的服务器。请房主改用独立服务器。");
            if (launching || webBusy > 0) throw new InvalidOperationException("服务器正在处理上一次操作，请稍后再试。");
            var map = maps.FirstOrDefault(m => m.Identity == identity) ?? throw new ArgumentException("没有这张地图。");
            if (!map.Local) throw new ArgumentException("这张地图还没下载完。");
            webBusy++;
            try
            {
                await SwitchExistingRoom(map, Settings(), RootBox.Text.Trim());
                snapshot = default;
                return JsonSerializer.Serialize(new { ok = true, map = map.Title });
            }
            finally { webBusy--; }
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
