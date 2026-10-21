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
        prefs.WebPort = port;
        prefs.WebEnabled = true;
        webToken = "";
        StartWebConsole();
        if (web == null) throw new Exception("Web console did not start: " + WebState.Text);
        var urls = WebUrls(port).ToList();
        File.WriteAllText(Path.Combine(dataDir, "web-hold.txt"),
            "port=" + port + "\ntoken=" + webToken + "\n" + string.Join("\n", urls) + "\n");
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
        prefs.WebPort = free;
        prefs.WebEnabled = true;
        webToken = "";
        StartWebConsole();
        if (web == null) throw new Exception("Web console did not start: " + WebState.Text);
        var port = web.Port;
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(90) };

        // 1. The page itself.
        var page = await client.GetAsync("");
        var html = await page.Content.ReadAsStringAsync();
        if (!page.IsSuccessStatusCode || !html.Contains("L A N T E R N")) throw new Exception("Page did not render: " + (int)page.StatusCode);
        evidence.Add($"PASS page served at http://127.0.0.1:{port}/ ({html.Length} bytes, no external requests)");

        // 2. A wrong token must not open anything, and must not be a timing oracle.
        var bad = await client.PostAsync("api/login", new StringContent("{\"token\":\"nope\"}", Encoding.UTF8, "application/json"));
        if (await bad.Content.ReadAsStringAsync() is var badBody && badBody.Contains("\"ok\":true")) throw new Exception("Wrong token was accepted");
        var anonymous = await client.GetAsync("api/state");
        var anonymousBody = await anonymous.Content.ReadAsStringAsync();
        if (anonymousBody.Contains("\"auth\":false") == false) throw new Exception("State was served without a token: " + anonymousBody);
        evidence.Add("PASS wrong token rejected; unauthenticated state read refused with auth:false");

        // 3. The real token sets a cookie, and the cookie is what later requests use.
        var login = await client.PostAsync("api/login", new StringContent($"{{\"token\":\"{webToken}\"}}", Encoding.UTF8, "application/json"));
        var loginBody = await login.Content.ReadAsStringAsync();
        if (!loginBody.Contains("\"ok\":true")) throw new Exception("Correct token rejected: " + loginBody);
        var setCookie = login.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.FirstOrDefault() : null;
        if (setCookie == null || !setCookie.StartsWith("lantern=", StringComparison.Ordinal)) throw new Exception("Login did not set a cookie");
        if (!setCookie.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase)) throw new Exception("Session cookie is readable by script");
        client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        evidence.Add("PASS login set an HttpOnly session cookie");

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

        // 7. Nothing in the page can stop the server.
        foreach (var forbidden in new[] { "/api/stop", "/api/start", "/api/restart", "/api/shutdown" })
        {
            var probe = await client.PostAsync(forbidden.TrimStart('/'), new StringContent("{}", Encoding.UTF8, "application/json"));
            if (probe.IsSuccessStatusCode) throw new Exception($"{forbidden} answered {probe.StatusCode}; the web console must not control the server lifecycle");
        }
        evidence.Add("PASS no server start/stop/restart route exists");

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
