using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using Lantern.Core;

namespace Lantern.App;

/// <summary>
/// End-to-end check of the LAN console: a real socket, a real browser-shaped
/// request, and the same code paths a friend would hit. A page that merely
/// compiles proves nothing — the failure modes here are authentication, the
/// HTTP framing, and whether a request can reach the UI thread at all.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Leaves the console listening on a fixed port so a human (or a screenshot)
    /// can look at the real page. Exits after the given number of seconds.
    /// </summary>
    async Task HoldWebConsole(int port, int seconds)
    {
        // A room screenshot needs a room: without one the hero frame and the
        // player table have nothing to draw, and the picture would prove nothing
        // about how they look in use.
        if (HostBox.SelectedIndex == 0) HostBox.SelectedIndex = 2;
        var holdRoot = RootBox.Text.Trim();
        if (await Task.Run(() => GameProcesses.Find(holdRoot, true)) == null)
        {
            RunText.Text = "网页控制台预览：先启动独立服务器…";
            await LaunchSelected();
        }
        smokeWebPort = port;
        StartWebConsole();
        if (web == null) throw new Exception("Web console did not start: " + WebState.Text);
        var urls = WebUrls(port).ToList();
        File.WriteAllText(Path.Combine(dataDir, "web-hold.txt"),
            "port=" + port + "\n" + string.Join("\n", urls) + "\n");
        // A browser needs the URL, not the loopback alias, but the loopback one
        // is what a same-machine screenshot can use.
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        StopWebConsole();
    }

    static async Task<JsonElement> ReadState(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("api/state"));
        return document.RootElement.Clone();
    }

    // The whole point of v0.5.35: a friend who opens the page with nothing
    // running can start a server, and clicking a map starts it on that map.
    // This has to begin with no server at all, so it refuses to run otherwise
    // rather than passing on a server someone else started.
    async Task TestWebLifecycle()
    {
        var evidence = new List<string>();
        if (HostBox.SelectedIndex == 0) HostBox.SelectedIndex = 2;
        var root = RootBox.Text.Trim();
        if (await Task.Run(() => GameProcesses.Find(root, true)) != null)
            throw new Exception("Lifecycle smoke needs no server running, but one already is. Close it and rerun.");

        int free;
        var scout = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        scout.Start(); free = ((System.Net.IPEndPoint)scout.LocalEndpoint).Port; scout.Stop();
        smokeWebPort = free;
        StartWebConsole();
        if (web == null) throw new Exception("Web console did not start: " + WebState.Text);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{web.Port}/"), Timeout = TimeSpan.FromSeconds(180) };

        var idle = await ReadState(client);
        if (idle.GetProperty("server").GetProperty("running").GetBoolean())
            throw new Exception("State reported a running server before anything was started");
        evidence.Add("PASS page says nothing is running before a start");

        // Start through the map card, which is what a friend actually clicks.
        var mapsJson = idle.GetProperty("maps");
        var target = mapsJson.EnumerateArray().First(m => m.GetProperty("official").GetBoolean());
        var identity = target.GetProperty("identity").GetString()!;
        var mapName = target.GetProperty("mapName").GetString()!;
        var started = await client.PostAsync("api/map",
            new StringContent($"{{\"identity\":\"{identity}\"}}", Encoding.UTF8, "application/json"));
        using var startedJson = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        if (!startedJson.RootElement.GetProperty("ok").GetBoolean())
            throw new Exception("Map click did not start a server: " + startedJson.RootElement);
        evidence.Add($"PASS map click started a server on {mapName}");

        // The claim is only true if the server itself reports that map.
        string? live = null;
        var deadline = DateTime.UtcNow.AddSeconds(150);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000);
            var state = await ReadState(client);
            var server = state.GetProperty("server");
            // busy must clear too: the launch reports the map before it returns,
            // and a restart sent in that window is refused.
            if (!state.GetProperty("busy").GetBoolean()
                && server.GetProperty("running").GetBoolean() && server.GetProperty("map").GetString() == mapName)
            { live = server.GetProperty("map").GetString(); break; }
        }
        if (live != mapName) throw new Exception($"Server never reported {mapName} after the map click");
        evidence.Add($"PASS the server came up on {live}, confirmed by its own status");

        var runningState = await ReadState(client);
        if (!runningState.GetProperty("server").GetProperty("running").GetBoolean())
            throw new Exception("Server reported running, then not running");

        // Restart the thing we just started.
        var restart = await client.PostAsync("api/server", new StringContent("{\"action\":\"restart\"}", Encoding.UTF8, "application/json"));
        using var restartJson = JsonDocument.Parse(await restart.Content.ReadAsStringAsync());
        if (!restartJson.RootElement.GetProperty("ok").GetBoolean())
            throw new Exception("Restart was refused: " + restartJson.RootElement);
        evidence.Add("PASS restart accepted while the server was running");

        string? afterRestart = null;
        deadline = DateTime.UtcNow.AddSeconds(150);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000);
            var state = await ReadState(client);
            var server = state.GetProperty("server");
            if (!state.GetProperty("busy").GetBoolean()
                && server.GetProperty("running").GetBoolean() && server.GetProperty("map").GetString() == mapName)
            { afterRestart = server.GetProperty("map").GetString(); break; }
        }
        if (afterRestart != mapName) throw new Exception("Server did not come back on its map after the restart");
        evidence.Add($"PASS restarted server back on {afterRestart}");

        File.WriteAllText(Path.Combine(dataDir, "web-lifecycle-smoke.txt"), string.Join("\n", evidence));
        StopWebConsole();
    }

    async Task TestWebConsole()
    {
        var evidence = new List<string>();
        // A command cannot be proven end to end without a server to run it.
        if (HostBox.SelectedIndex == 0) HostBox.SelectedIndex = 2;
        var webRoot = RootBox.Text.Trim();
        if (await Task.Run(() => GameProcesses.Find(webRoot, true)) == null)
        {
            RunText.Text = "网页控制台核验：先启动独立服务器…";
            await LaunchSelected();
        }
        // Any fixed port is somebody else's port on some machine; ask the OS for a free one.
        int free;
        var scout = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        scout.Start();
        free = ((System.Net.IPEndPoint)scout.LocalEndpoint).Port;
        scout.Stop();
        smokeWebPort = free;
        StartWebConsole();
        if (web == null) throw new Exception("Web console did not start: " + WebState.Text);
        var port = web.Port;
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(90) };

        // 1. The page itself.
        var page = await client.GetAsync("");
        var html = await page.Content.ReadAsStringAsync();
        if (!page.IsSuccessStatusCode || !html.Contains("L A N T E R N")) throw new Exception("Page did not render: " + (int)page.StatusCode);
        evidence.Add($"PASS page served at http://127.0.0.1:{port}/ ({html.Length} bytes, no external requests)");

        // 2. No password any more: state must answer on a plain request. There is
        //    no login route left to try, so asking for one must 404 rather than
        //    quietly succeed.
        var fresh = await client.GetAsync("api/state");
        var freshBody = await fresh.Content.ReadAsStringAsync();
        if (!freshBody.Contains("\"ok\":true")) throw new Exception("State was refused on a plain request: " + freshBody);
        if (freshBody.Contains("\"auth\":false")) throw new Exception("State still advertises an auth gate: " + freshBody);
        evidence.Add("PASS state served without any password");
        var login = await client.PostAsync("api/login", new StringContent("{}", Encoding.UTF8, "application/json"));
        if (login.IsSuccessStatusCode) throw new Exception("A login route still exists");
        evidence.Add("PASS no login route remains");

        // 4. State must describe the real server, not a hopeful default.
        var state = JsonDocument.Parse(await client.GetStringAsync("api/state"));
        var root = state.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) throw new Exception("State reported failure: " + root);
        var server = root.GetProperty("server");
        var running = server.GetProperty("running").GetBoolean();
        var mapsJson = root.GetProperty("maps");
        if (mapsJson.GetArrayLength() == 0) throw new Exception("State listed no switchable maps");
        evidence.Add($"PASS state read: running={running}, map='{server.GetProperty("map").GetString()}', " +
            $"humans={server.GetProperty("humans").GetInt32()}, maps={mapsJson.GetArrayLength()}, capacity={server.GetProperty("capacity")}");

        // 5. An unknown map must be refused by name, not silently ignored.
        var unknown = await client.PostAsync("api/map", new StringContent("{\"identity\":\"nope:nope\"}", Encoding.UTF8, "application/json"));
        // The body is JSON, so Chinese arrives backslash-u escaped; compare the
        // decoded message, not the wire bytes.
        using var unknownJson = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        var unknownError = unknownJson.RootElement.TryGetProperty("error", out var refused) ? refused.GetString() ?? "" : "";
        if (unknownJson.RootElement.GetProperty("ok").GetBoolean() || !unknownError.Contains("没有这张地图"))
            throw new Exception("Unknown map was not refused clearly: " + unknownError);
        evidence.Add("PASS unknown map refused with an explanation: " + unknownError);

        // 5b. A real switch through the web API, verified against the server's own
        //     reported map — not against the answer the API gave.
        var before = (await ReadState(client)).GetProperty("server").GetProperty("map").GetString();
        var target = maps.FirstOrDefault(m => m.Official && m.MapName != before && m.MapName is "de_dust2" or "de_inferno")
            ?? maps.First(m => m.Official && m.MapName != before);
        var switchResponse = await client.PostAsync("api/map",
            new StringContent($"{{\"identity\":\"{target.Identity}\"}}", Encoding.UTF8, "application/json"));
        using var switchJson = JsonDocument.Parse(await switchResponse.Content.ReadAsStringAsync());
        var switchOk = switchJson.RootElement.GetProperty("ok").GetBoolean();
        var switchError = switchJson.RootElement.TryGetProperty("error", out var se) ? se.GetString() ?? "" : "";
        if (!switchOk) throw new Exception($"Web map switch to {target.MapName} failed: {switchError}");
        string? actualMap = null;
        var switchDeadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < switchDeadline)
        {
            await Task.Delay(1500);
            try { actualMap = (await ReadState(client)).GetProperty("server").GetProperty("map").GetString(); }
            catch { continue; }
            if (actualMap == target.MapName) break;
        }
        if (actualMap != target.MapName)
            throw new Exception($"Server reports map '{actualMap}', the web switch claimed '{target.MapName}'");
        evidence.Add($"PASS web map switch {before} -> {target.MapName} confirmed by the server's own status");

        // 5c. Covers are real bytes from two different stores: an embedded PNG for
        //     official maps, a cached JPEG for workshop maps. Serving a broken
        //     <img> would look like a working page in every other check here.
        var withCover = maps.Where(m => m.Local).Select(m => new
        {
            m.Identity,
            m.MapName,
            m.Official,
            Bytes = m.Official
                ? (OfficialMaps.HasCover(m.MapName) ? 1 : 0)
                : (File.Exists(Path.Combine(dataDir, "covers", m.Id + ".jpg")) ? 1 : 0),
        }).FirstOrDefault(x => x.Bytes == 1);
        if (withCover == null) throw new Exception("No map with a cover is available to test");
        var cover = await client.GetAsync("api/cover?identity=" + Uri.EscapeDataString(withCover.Identity));
        var coverBytes = await cover.Content.ReadAsByteArrayAsync();
        var coverType = cover.Content.Headers.ContentType?.MediaType ?? "";
        if (!cover.IsSuccessStatusCode) throw new Exception("Cover request failed: " + (int)cover.StatusCode);
        if (coverBytes.Length < 512) throw new Exception($"Cover is only {coverBytes.Length} bytes");
        // PNG and JPEG both start with a fixed signature; anything else means the
        // bytes are not an image at all (an HTML error page, for instance).
        bool isPng = coverBytes.Length > 8 && coverBytes[0] == 0x89 && coverBytes[1] == 0x50 && coverBytes[2] == 0x4E && coverBytes[3] == 0x47;
        bool isJpeg = coverBytes.Length > 3 && coverBytes[0] == 0xFF && coverBytes[1] == 0xD8 && coverBytes[2] == 0xFF;
        if (!isPng && !isJpeg) throw new Exception("Cover bytes are neither PNG nor JPEG");
        if (coverType != (isPng ? "image/png" : "image/jpeg")) throw new Exception($"Cover served as {coverType} but bytes are {(isPng ? "PNG" : "JPEG")}");
        evidence.Add($"PASS cover {withCover.Identity} served as {coverType}, {coverBytes.Length} bytes, signature verified");

        // A map with no cover must say so instead of returning an empty 200 the
        // browser would render as a broken image.
        var bare = maps.Where(m => m.Local).FirstOrDefault(m => m.Official && !OfficialMaps.HasCover(m.MapName));
        if (bare != null)
        {
            var missing = await client.GetAsync("api/cover?identity=" + Uri.EscapeDataString(bare.Identity));
            if (missing.IsSuccessStatusCode) throw new Exception("A coverless map answered 200 for its cover");
            evidence.Add($"PASS coverless map {bare.Identity} answered {(int)missing.StatusCode} instead of an empty image");
        }

        // 6. A command must actually reach the server and its output must come back.
        // Not "LANTERN_...": GameConsole treats that prefix as its own protocol
        // traffic and keeps it out of the display log, which is exactly what this
        // check is watching.
        var marker = "webecho" + Guid.NewGuid().ToString("N")[..8];
        var command = await client.PostAsync("api/command", new StringContent($"{{\"command\":\"echo {marker}\"}}", Encoding.UTF8, "application/json"));
        var commandBody = await command.Content.ReadAsStringAsync();
        if (!commandBody.Contains("\"ok\":true")) throw new Exception("Command was refused: " + commandBody);
        // The launcher logs "> <command>" when it sends, so a line that merely
        // contains the marker can be our own outgoing echo. Only the engine's
        // reply — the marker with no "> " prefix — proves the round trip.
        string? seen = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && seen == null)
        {
            await Task.Delay(600);
            var poll = JsonDocument.Parse(await client.GetStringAsync("api/state?since=0"));
            foreach (var line in poll.RootElement.GetProperty("log").EnumerateArray())
            {
                var text = line.GetString() ?? "";
                if (text.Contains(marker, StringComparison.Ordinal) && !text.TrimStart().StartsWith("> ", StringComparison.Ordinal))
                { seen = text; break; }
            }
        }
        if (seen == null) throw new Exception($"Command {marker} never came back from the engine within 20s (only our own outgoing echo was seen)");
        evidence.Add($"PASS command reached the engine and its reply came back: {seen!.Trim()}");

        // 6b. The quick-action chips are a closed list: an unknown name is refused
        // rather than silently doing nothing, and a known one reaches the engine.
        var bogus = await client.PostAsync("api/quick", new StringContent("{\"action\":\"rm -rf\"}", Encoding.UTF8, "application/json"));
        var bogusBody = await bogus.Content.ReadAsStringAsync();
        if (bogusBody.Contains("\"ok\":true")) throw new Exception("Quick endpoint accepted an unknown action: " + bogusBody);
        evidence.Add("PASS unknown quick action rejected: " + bogusBody.Trim());

        var swap = await client.PostAsync("api/quick", new StringContent("{\"action\":\"swap\"}", Encoding.UTF8, "application/json"));
        var swapBody = await swap.Content.ReadAsStringAsync();
        if (!swapBody.Contains("\"ok\":true")) throw new Exception("Quick action swap was refused: " + swapBody);
        if (!swapBody.Contains(QuickCommands.SwapTeams)) throw new Exception("Quick action did not report the command it sent: " + swapBody);
        evidence.Add("PASS quick action swap reached the engine: " + QuickCommands.SwapTeams);

        // 6c. Start and restart exist, but only one applies at a time: starting an
        // already-running server is refused rather than spawning a second one,
        // and an unknown action is refused outright.
        var dup = await client.PostAsync("api/server", new StringContent("{\"action\":\"start\"}", Encoding.UTF8, "application/json"));
        // Chinese comes back \u-escaped, so compare the decoded message.
        using var dupJson = JsonDocument.Parse(await dup.Content.ReadAsStringAsync());
        var dupOk = dupJson.RootElement.GetProperty("ok").GetBoolean();
        var dupError = dupJson.RootElement.TryGetProperty("error", out var dupErr) ? dupErr.GetString() ?? "" : "";
        if (dupOk) throw new Exception("Start was accepted while the server was already running");
        if (!dupError.Contains("已经在运行")) throw new Exception("Start refused without saying why: " + dupError);
        evidence.Add("PASS starting an already-running server is refused: " + dupError);

        var bogusServer = await client.PostAsync("api/server", new StringContent("{\"action\":\"kill\"}", Encoding.UTF8, "application/json"));
        using var bogusServerJson = JsonDocument.Parse(await bogusServer.Content.ReadAsStringAsync());
        if (bogusServerJson.RootElement.GetProperty("ok").GetBoolean())
            throw new Exception("Unknown server action was accepted");
        var bogusServerError = bogusServerJson.RootElement.TryGetProperty("error", out var bsErr) ? bsErr.GetString() ?? "" : "";
        evidence.Add("PASS unknown server action refused: " + bogusServerError);

        // 7. Nothing in the page can stop the server.
        foreach (var forbidden in new[] { "/api/stop", "/api/shutdown" })
        {
            var probe = await client.PostAsync(forbidden.TrimStart('/'), new StringContent("{}", Encoding.UTF8, "application/json"));
            if (probe.IsSuccessStatusCode) throw new Exception($"{forbidden} answered {probe.StatusCode}; the web console must not control the server lifecycle");
        }
        evidence.Add("PASS no stop or shutdown route exists; starting and restarting are allowed");

        // 8. A request line the parser cannot trust must not be guessed at.
        if (MiniHttp.TryRequestLine("GET", out _, out _, out _)) throw new Exception("Truncated request line was accepted");
        if (MiniHttp.TryRequestLine("GET relative HTTP/1.1", out _, out _, out _)) throw new Exception("Relative request target was accepted");
        if (!MiniHttp.TryRequestLine("GET /api/state?since=7 HTTP/1.1", out var method, out var path, out var query)
            || method != "GET" || path != "/api/state" || MiniHttp.ParseQuery(query)["since"] != "7")
            throw new Exception("Valid request line did not parse");
        evidence.Add("PASS request line parsing rejects junk and splits query strings");

        var requests = web.Requests;
        if (requests < 8) throw new Exception("Too few requests reached the socket layer: " + requests);
        evidence.Add($"PASS {requests} requests served over a real TCP socket on port {port}");

        File.WriteAllLines(Path.Combine(dataDir, "web-smoke.txt"), evidence);
        StopWebConsole();
    }
}
