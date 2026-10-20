using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lantern.Core;

if (args.Contains("--population-live"))
{
    var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var console = new GameConsole();
    console.Output += line => log.Enqueue(line);
    await console.Connect(2121, File.ReadAllText(args[Array.IndexOf(args, "--key") + 1]).Trim());
    async Task<RoomStatus> Snapshot(string label) { var r = await console.ReadStatus(); Console.WriteLine(label + " " + JsonSerializer.Serialize(r)); return r; }
    await console.Send("jointeam 3"); await Task.Delay(2000);
    await console.Send(QuickCommands.RemoveBots); await QuickCommands.WaitBots(console, 0);
    await console.Send("bot_auto_vacate 0; mp_limitteams 0; mp_autoteambalance 0; bot_quota_mode normal; bot_join_team T; bot_quota 2");
    await Task.Delay(2000); var before = await Snapshot("BEFORE_CT");
    await TeamBots.Add(console, "ct", 1, 64); await Task.Delay(3000); var after = await Snapshot("AFTER_CT");
    await console.Send("bot_quota 0; bot_kick t"); await Task.Delay(1500); await Snapshot("ONLY_CT_AFTER_KICK_T");
    await console.Send(QuickCommands.RemoveBots); await Task.Delay(1000);
    await TeamBots.Add(console, "t", 1, 64); await Task.Delay(2000); await Snapshot("AFTER_T");
    await console.Send("bot_quota 0; bot_kick ct"); await Task.Delay(1000); await Snapshot("ONLY_T_AFTER_KICK_CT");
    foreach (var size in new[] { 5, 6, 7 })
    {
        await QuickCommands.FillTeams(console, 0, 1, size); await Snapshot($"TEAM_{size}V{size}");
    }
    await QuickCommands.FillTeams(console, 0, 1, 5); await Snapshot("FINAL_5V5");
    Directory.CreateDirectory("artifacts/live-population");
    File.WriteAllText("artifacts/live-population/game-output.txt", string.Join("", log));
    return;
}

if (args.Contains("--room-live"))
{
    using var client = new GameConsole();
    await client.Connect(port: args.Contains("--port") ? int.Parse(args[Array.IndexOf(args, "--port") + 1]) : 2121, password: File.ReadAllText(args[Array.IndexOf(args, "--key") + 1]).Trim());
    var status = await client.ReadStatus();
    Console.WriteLine(JsonSerializer.Serialize(status));
    return;
}

if (args.Contains("--console-live"))
{
    var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var console = new GameConsole();
    console.Output += line => output.Enqueue(line);
    await console.Connect(password: args.Contains("--key") ? File.ReadAllText(args[Array.IndexOf(args, "--key") + 1]).Trim() : "");
    Console.WriteLine("PASS real CS2 NetCon echo handshake");
    foreach (var command in new[] { "echo LANTERN_TEST_REMOVE", QuickCommands.RemoveBots, "bot_quota", "status", "echo LANTERN_TEST_BALANCE", QuickCommands.Balance(6), "bot_quota_mode", "bot_quota", "mp_autoteambalance", "mp_limitteams", "echo LANTERN_TEST_RESTART", QuickCommands.Restart })
    { await console.Send(command); await Task.Delay(400); }
    await Task.Delay(2500); await console.Send("status"); await Task.Delay(600);
    var log = string.Join("", output); Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/console-live.txt", log);
    Console.WriteLine("Real console output saved to artifacts/console-live.txt");
    foreach (var line in log.Split('\n').Where(l => l.Contains("bot_quota") || l.Contains("mp_auto") || l.Contains("mp_limit") || l.Contains("restart", StringComparison.OrdinalIgnoreCase) || l.Contains("map ", StringComparison.OrdinalIgnoreCase))) Console.WriteLine(line);
    return;
}

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS " + message); passed++; }
void Reject(Action action, string message) { try { action(); } catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or InvalidOperationException) { Check(true, message); return; } throw new Exception("Did not reject: " + message); }
var temp = Path.Combine(Path.GetTempPath(), "lantern-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
try
{
    var variants = new[] {
        new MapEntry { Id = "123", MapName = "de_example", Title = "Same", FilePath = "fixture.vpk" },
        new MapEntry { Id = "123", MapName = "de_example_test", Title = "Same", FilePath = "fixture.vpk" },
        new MapEntry { Id = "456", MapName = "de_example", Title = "Same", FilePath = "fixture.vpk" },
        new MapEntry { Official = true, MapName = "de_example", FilePath = "fixture.vpk" }
    };
    Check(MapCatalog.Cards(variants).Count == 3, "one card per workshop ID while distinct packages and official maps remain separate");
    Check(MapCatalog.Cards(variants, "123:de_example_test")[0].MapName == "de_example_test", "remember explicit package variant across refresh");
    Check(MapCatalog.Cards(variants.Where(m => m.MapName.EndsWith("_test")))[0].MapName == "de_example_test", "search can discover hidden package variants");
    var plainClient = GameEndpointParser.Parse(10, "\"C:\\Games\\CS2\\cs2.exe\" -steam -perfectworld");
    Check(!plainClient.Dedicated && plainClient.Port == null, "ordinary Steam launch is not assumed to expose 2121");
    var customClient = GameEndpointParser.Parse(11, "\"C:\\Games With Spaces\\cs2.exe\" -steam -netconport 22123 -netconpassword abc123");
    Check(customClient.Port == 22123 && customClient.Password == "abc123" && !customClient.Dedicated, "custom client port and auth read from actual launch arguments");
    Check(!customClient.ToString().Contains("abc123"), "endpoint diagnostic never prints password");
    var dedicatedEndpoint = GameEndpointParser.Parse(12, "cs2.exe -dedicated -netconport=2122 -netconpassword=xyz123");
    Check(dedicatedEndpoint.Dedicated && dedicatedEndpoint.Port == 2122, "dedicated process is classified independently of window title");
    Check(GameEndpointParser.Parse(13, "cs2.exe -netconport 99999").Port == null, "invalid terminal port is not guessed");
    Check(GameEndpointParser.Parse(14, "\"C:\\-dedicated\\cs2.exe\" -steam").Dedicated == false, "path text cannot turn client into dedicated process");
    // Isolated fake NetCon: random loopback port, never contacts an installed game.
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var fakePort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var simulator = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync();
        using var reader = new StreamReader(peer.GetStream());
        using var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
        int queries = 0;
        while (await reader.ReadLineAsync() is string line)
        {
            if (line.StartsWith("echo ")) await writer.WriteLineAsync(line[5..]);
            if (line.StartsWith("status_json; echo "))
            {
                await writer.WriteLineAsync("unrelated log before status");
                await writer.WriteLineAsync("{");
                await writer.WriteLineAsync(++queries == 1 ? "\"server\":{\"startup_ServerModuleInit\":1325}" : "\"server\":{\"map\":\"test_map\",\"clients_human\":2,\"clients_bot\":1,\"udp_port\":27016}");
                await writer.WriteLineAsync("}" + line[18..]);
            }
        }
    });
    using (var fakeClient = new GameConsole())
    {
        await fakeClient.Connect(fakePort);
        bool inactive = false;
        try { await fakeClient.ReadStatus(); } catch (InvalidOperationException e) { inactive = e.Message.Contains("尚未加载地图"); }
        Check(inactive, "fake NetCon inactive state returns useful diagnosis without disconnecting");
        var activeRoom = await fakeClient.ReadStatus();
        Check(activeRoom.Map == "test_map" && activeRoom.Humans == 2 && activeRoom.Bots == 1, "fake NetCon recovers from inactive to active with combined closing brace and marker");
    }
    await simulator.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
    var teamListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    teamListener.Start();
    var teamCommands = new List<string>();
    var teamLines = new List<string>();
    var teamSimulator = Task.Run(async () =>
    {
        using var peer = await teamListener.AcceptTcpClientAsync();
        using var reader = new StreamReader(peer.GetStream());
        using var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
        int bots = 0;
        while (await reader.ReadLineAsync() is string line)
        {
        teamLines.Add(line);
        foreach (var part in line.Split(';').Select(p => p.Trim()))
        {
            teamCommands.Add(part);
            if (part.StartsWith("echo ")) await writer.WriteLineAsync(part[5..]);
            else if (part == "status_json")
                await writer.WriteLineAsync("{\n\"server\":{\"map\":\"test_map\",\"clients_human\":1,\"clients_bot\":" + bots + ",\"udp_port\":27016}\n}");
            else if (part == "status")
            {
                await writer.WriteLineAsync("---------players--------");
                for (int id = 1; id <= bots; id++) await writer.WriteLineAsync($" {id} BOT 0 0 active 0 'Bot{id}'");
                await writer.WriteLineAsync("#end");
            }
            else if (part == "log") await writer.WriteLineAsync("not currently logging");
            else if (part == "bot_add_ct")
            {
                if (bots == 10) await writer.WriteLineAsync("Could not add bot to the game: Team is full");
                else { bots++; await writer.WriteLineAsync($"\"Bot{bots}<{bots}><BOT>\" switched from team <Unassigned> to <CT>"); }
            }
        }
        }
    });
    using (var client = new GameConsole())
    {
        await client.Connect(((System.Net.IPEndPoint)teamListener.LocalEndpoint).Port);
        var result = await TeamBots.Add(client, "ct", 10, 32);
        Check(result.Added == 10 && result.Requested == 10, "all ten requested bots are verified instead of stopping after one");
        result = await TeamBots.Add(client, "ct", 3, 32);
        Check(result.Added == 0 && result.Detail.Contains("已满"), "batch stops at full CT team");
    }
    await teamSimulator.WaitAsync(TimeSpan.FromSeconds(5)); teamListener.Stop();
    Check(teamCommands.Count(c => c == "bot_add_ct") == 11 && !teamCommands.Contains("bot_add_t")
        && teamCommands.Last(c => c.StartsWith("bot_quota ")) == "bot_quota 10" && teamCommands.Contains("log off"), "full team is not retried and quota is restored");
    Check(teamLines.Where(c => c.StartsWith("bot_add_ct")).All(c => c.Contains("; bot_quota ")), "quota correction shares the addition command buffer before engine simulation");
    Check(TeamBots.ConfirmSurvivor(new() {4}, new() {4, 6}, new Dictionary<int,string>{{5,"t"},{6,"t"}}, "t"), "verify surviving bot when first engine addition was removed");
    Check(!TeamBots.ConfirmSurvivor(new() {4}, new() {4, 6}, new Dictionary<int,string>{{6,"ct"}}, "t"), "wrong team survivor is rejected");
    Check(TeamBots.IsExactAddition(new() { 18, 19 }, new() { 18, 19, 272 }, 16), "reused bot slots match generation-bearing status userid");
    Check(!TeamBots.IsExactAddition(new() { 18, 19 }, new() { 18, 272 }, 16), "replacement of existing bot is not counted as successful addition");
    var vdf = Kv.Parse("//header\n\"libraryfolders\" { \"0\" { \"path\" \"D:\\\\Steam Library\" } }");
    Check(vdf.Node("libraryfolders")!.Node("0")!.Get("path") == @"D:\Steam Library", "Steam escaped paths and comments");
    Reject(() => Kv.Parse("a { b"), "truncated VDF rejected");
    Check(Workshop.ParseId("https://steamcommunity.com/sharedfiles/filedetails/?id=3070293560&searchtext=hi") == "3070293560", "workshop link query extraction");
    Check(Workshop.ParseIds("https://steamcommunity.com/sharedfiles/filedetails/?id=3623775808https://steamcommunity.com/sharedfiles/filedetails/?id=3636075272").SequenceEqual(new[] { "3623775808", "3636075272" }), "concatenated workshop links parsed separately");
    var subscriptionFile = Path.Combine(temp, "730_subscriptions.vdf");
    File.WriteAllText(subscriptionFile, "subscribedfiles { appid 730 0 { publishedfileid 3623775808 } 1 { publishedfileid 3636075272 disabled_locally 0 } 2 { publishedfileid 1234567 disabled_locally 1 } }");
    Check(Subscriptions.Read(subscriptionFile).SequenceEqual(new[] { "3623775808", "3636075272" }), "subscription cache exposes not-yet-downloaded items, excludes disabled entries");
    var emptyInstall = new Installation(temp, Array.Empty<string>());
    var initialCache = MapChanges.Fingerprint(emptyInstall, Array.Empty<string>(), new[] { subscriptionFile });
    File.AppendAllText(subscriptionFile, "\n// Steam subscription update");
    Check(initialCache != MapChanges.Fingerprint(emptyInstall, Array.Empty<string>(), new[] { subscriptionFile }), "subscription-only change triggers automatic detection without VPK files");
    var packet = ConsoleProtocol.Command("echo 中文");
    Check(Encoding.UTF8.GetString(packet) == "echo 中文\n", "NetCon UTF8 line framing");
    Reject(() => ConsoleProtocol.Command("status\nquit"), "NetCon embedded newlines rejected");
    Reject(() => ConsoleProtocol.Command(new string('x', 8193)), "NetCon oversized command rejected");
    Check(QuickCommands.RemoveBots.StartsWith("bot_quota 0;") && QuickCommands.RemoveBots.EndsWith("bot_kick"), "remove bots prevents automatic refill");
    Check(TeamBots.Command("ct") == "bot_add_ct" && TeamBots.Command("t") == "bot_add_t", "explicit team additions never grow quota as a substitute");
    Reject(() => TeamBots.Command("ct;quit"), "reject invalid bot team");
    Check(TeamBots.Joined("L 10/13/2026 - 14:00:37: \"McCoy<18><BOT>\" switched from team <Unassigned> to <CT>") == (18, "ct"), "confirm CT team from actual join event");
    Check(TeamBots.Joined("\"Syfers<19><BOT>\" switched from team <Unassigned> to <TERRORIST>") == (19, "t"), "confirm T team from actual join event");
    Check(TeamBots.Joined("Could not add bot to the game: Team is full") == null, "full team is never success");
    Check(TeamBots.BotIds("---------players--------\n 18 BOT 0 0 active 0 'McCoy'\n65280 01:10 0 0 active 0 [U:1:10] 'Human'\n#end").SetEquals(new[] {18}), "bot roster excludes human IDs");
    Reject(() => TeamBots.BotIds("18 BOT 0 0 active 0 'McCoy'"), "truncated roster rejected");
    Check(QuickCommands.Balance(10).Contains("bot_quota_mode fill; bot_quota 10"), "balance quota counts human players plus bots");
    Reject(() => QuickCommands.Balance(0), "balance invalid quota rejected");
    var fill = QuickCommands.TeamFill(3, 4);
    Check(fill.Size == 4 && fill.T == 1 && fill.Ct == 0, "3 vs 4 humans adds one T bot for 4v4");
    var reverse = QuickCommands.TeamFill(5, 2);
    var seven = QuickCommands.TeamFill(3, 4, 7);
    Check(seven.T == 4 && seven.Ct == 3 && seven.Size == 7, "explicit 7v7 fills each team without reserving a bot slot");
    Check(QuickCommands.TeamFill(1, 0, 5).Ct == 5 && QuickCommands.TeamFill(1, 0, 6).T == 5, "explicit 5v5 and 6v6 targets include humans");
    Reject(() => QuickCommands.TeamFill(6, 3, 5), "team target cannot remove existing humans");
    Check(QuickCommands.Balance(14).Contains("bot_auto_vacate 0") && QuickCommands.Balance(14).Contains("bot_quota 14"), "continuous fill does not reserve a vacant slot");
    Check(GameEndpointParser.Parse(1, "cs2.exe -maxplayers 10").RequestedMaxPlayers == 10, "existing process retains its original capacity request");
    Check(reverse.Size == 5 && reverse.Ct == 3 && reverse.T == 0, "5 vs 2 adds three CT bots without moving humans");
    Reject(() => RoomStatusParser.Parse("{\"server\":{\"startup_ServerModuleInit\":1325}}"), "inactive real dedicated status is not a ready room");
    Reject(() => RoomStatusParser.Parse("{}"), "missing server is not a ready room");
    Reject(() => RoomStatusParser.Parse("{\"server\":{\"map\":\"de_dust2\"}}"), "missing counts do not become zero players");
    var roomFixture = RoomStatusParser.Parse("{\"server\":{\"map\":\"crazy_chickens\",\"addon\":\"3815559839\",\"clients_human\":0,\"clients_bot\":0,\"udp_port\":27016}}");
    var noisyRoom = RoomStatusParser.Parse("{\"server\":{\"map\":\"de_dust2\",\"clients_human\":1,\"clients_bot\":0}}\n[#123 P2P SDR] connected\n");
    Check(noisyRoom.Map == "de_dust2" && noisyRoom.Humans == 1, "asynchronous network log after status JSON does not break room parsing");
    Check(RoomStatusParser.ActiveHumans("---------players--------\n65280 00:14 0 0 active 786432 [U:1:123]:0 'Player'\n65281 00:00 0 0 spawning 786432 [U:1:456]:0 'Loading'\n2 00:10 0 0 active 0 BOT 'Bot'\n#end") == 1, "only fully active humans count toward safe dedicated map change");
    Reject(() => RoomStatusParser.ActiveHumans("status unavailable"), "incomplete player list cannot authorize dedicated map change");
    Check(roomFixture.Map == "crazy_chickens" && roomFixture.Port == 27016 && roomFixture.Identity == "", "active room works without Steam identity");
    Check(QuickCommands.TeamFill(4, 4).T == 0 && QuickCommands.TeamFill(4, 4).Ct == 0, "equal teams need no bots");
    Reject(() => QuickCommands.TeamFill(0, 0), "empty server fill rejected");
    Reject(() => QuickCommands.TeamFill(-1, 3), "negative team count rejected");
    Reject(() => Workshop.ParseId("https://steamcommunity.com.evil.test/sharedfiles/filedetails/?id=3070293560"), "spoofed workshop domain rejected");
    Reject(() => Workshop.ParseId("3070293560;quit"), "workshop console injection rejected");
    Reject(() => Workshop.ParseId("00000"), "zero workshop identity rejected");
    Reject(() => Workshop.ParseId("99999999999999999999"), "overflow workshop ID rejected");

    var library = Path.Combine(temp, "Steam Library"); var appRoot = Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive");
    var dir = Path.Combine(library, "steamapps", "workshop", "content", "730", "3070293560"); Directory.CreateDirectory(dir);
    var vpkPath = Path.Combine(dir, "3070293560.vpk");
    using (var tree = new MemoryStream())
    {
        using var t = new BinaryWriter(tree, Encoding.UTF8, true);
        void Z(string s) { t.Write(Encoding.UTF8.GetBytes(s)); t.Write((byte)0); }
        Z("vmap_c"); Z("maps"); Z("de_fixture"); t.Write(0u); t.Write((ushort)3); t.Write((ushort)0x7FFF); t.Write(0u); t.Write(0u); t.Write((ushort)0xFFFF); t.Write(new byte[3]); Z(""); Z(""); Z("");
        using var f = new BinaryWriter(File.Create(vpkPath)); f.Write(0x55AA1234u); f.Write(2u); f.Write((uint)tree.Length); f.Write(new byte[16]); f.Write(tree.ToArray());
    }
    File.WriteAllText(Path.Combine(dir, "publish_data.txt"), "\"publish_data\" { \"title\" \"测试地图\" }");
    Check(Vpk.ReadMapNames(vpkPath).SequenceEqual(new[] { "de_fixture" }), "VPK v2 index with preload bytes");
    var nested = Path.Combine(temp, "nested_dir.vpk");
    using (var tree = new MemoryStream())
    {
        using var t = new BinaryWriter(tree, Encoding.UTF8, true);
        void Z(string s) { t.Write(Encoding.UTF8.GetBytes(s)); t.Write((byte)0); }
        Z("vpk"); Z("maps");
        foreach (var name in new[] { "arena_skybox", "arena" }) { Z(name); t.Write(0u); t.Write((ushort)0); t.Write((ushort)0); t.Write(0u); t.Write(10u); t.Write((ushort)0xFFFF); }
        Z(""); Z(""); Z("");
        using var f = new BinaryWriter(File.Create(nested)); f.Write(0x55AA1234u); f.Write(2u); f.Write((uint)tree.Length); f.Write(new byte[16]); f.Write(tree.ToArray());
    }
    Reject(() => Vpk.ReadMapNames(nested), "missing split VPK archive rejected");
    File.WriteAllBytes(Path.Combine(temp, "nested_000.vpk"), new byte[10]);
    Check(Vpk.ReadMapNames(nested).SequenceEqual(new[] { "arena" }), "nested workshop maps found and auxiliary skybox excluded");
    Check(Vpk.DeclaredMaps("{ maps = [\"de_dust2\",] }").SequenceEqual(new[]{"de_dust2"}), "addon playable base map overrides auxiliary spawn-group name");
    Check(Vpk.DeclaredMaps("{ maps = [\"bad;quit\",\"de_dust2\",\"de_dust2\"] }").SequenceEqual(new[]{"de_dust2"}), "declared map names are validated and deduplicated");
    var overlay = Path.Combine(temp, "overlay.vpk");
    var metadata = Encoding.UTF8.GetBytes("{ maps = [\"de_dust2\",] }");
    using (var tree = new MemoryStream())
    {
        using var t = new BinaryWriter(tree, Encoding.UTF8, true);
        void Z(string s) { t.Write(Encoding.UTF8.GetBytes(s)); t.Write((byte)0); }
        Z("txt"); Z(" "); Z("addoninfo"); t.Write(0u); t.Write((ushort)0); t.Write((ushort)0x7FFF); t.Write(0u); t.Write((uint)metadata.Length); t.Write((ushort)0xFFFF); Z(""); Z("");
        Z("vpk"); Z("maps"); Z("prophunt_dust2"); t.Write(0u); t.Write((ushort)0); t.Write((ushort)0x7FFF); t.Write(0u); t.Write(0u); t.Write((ushort)0xFFFF); Z(""); Z(""); Z("");
        using var f = new BinaryWriter(File.Create(overlay)); f.Write(0x55AA1234u); f.Write(2u); f.Write((uint)tree.Length); f.Write(new byte[16]); f.Write(tree.ToArray()); f.Write(metadata);
    }
    Check(Vpk.ReadMapNames(overlay).SequenceEqual(new[]{"de_dust2"}), "embedded addoninfo read from VPK data identifies actual playable entry");
    var broken = Path.Combine(dir, "broken.vpk"); File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4 });
    Reject(() => Vpk.ReadMapNames(broken), "corrupt VPK rejected");
    var scan = Discovery.Scan(new Installation(appRoot, new[] { library }));
    Check(scan.Maps.Count == 1 && scan.Maps[0].Title == "测试地图" && scan.Warnings.Count == 1, "scan survives corrupt file and loads local title");
    var map = scan.Maps[0]; var settings = new HostSettings { ConsolePassword = new string('a', 48), Kind = HostKind.Listen, ModeIndex = 1, Bots = 3, ApplyRules = true };
    var roots = Discovery.WorkshopRoots(new Installation(appRoot, new[] { library }));
    Check(MapDeletion.Package(map, roots) == dir, "deletion resolves selected workshop package only");
    Reject(() => MapDeletion.Package(new MapEntry { Official = true, FilePath = vpkPath }, roots), "official map deletion forbidden");
    Reject(() => MapDeletion.Package(new MapEntry { Id = map.Id, FilePath = Path.Combine(temp, "outside.vpk") }, roots), "outside-root deletion forbidden");
    Reject(() => MapDeletion.Package(new MapEntry { Id = "1234567", FilePath = vpkPath }, roots), "mismatched workshop ID forbidden");
    var installFixture = new Installation(appRoot, new[] { library });
    var beforeChange = MapChanges.Fingerprint(installFixture, Array.Empty<string>());
    File.WriteAllBytes(Path.Combine(dir, "downloading_000.vpk"), new byte[1]);
    Check(MapChanges.Fingerprint(installFixture, Array.Empty<string>()) != beforeChange, "automatic refresh detects new VPK metadata");
    var selectionSet = new HashSet<MapEntry> { map };
    map.Title = "Updated metadata"; map.Tags = "Map · Cs2"; map.PreviewPath = "updated.jpg";
    Check(selectionSet.Contains(map), "selected item hash remains stable after mutable metadata updates");
    var plan = Hosting.Build(map, settings, "test");
    Check(Hosting.Build(map, settings with { QuotaMode = "fill", Bots = 10, MaxPlayers = 10 }, "test").Rules.Contains("bot_auto_vacate 0"), "fill total can equal capacity and disables reserved slot");
    var deferred = Hosting.DeferredClientArguments(plan);
    Check(!deferred.Contains("+exec") && deferred.Contains("-netconport") && deferred.Contains("-netconpassword"), "repair defers map until authenticated console while preserving terminal arguments");
    Reject(() => Hosting.DeferredClientArguments(Hosting.Build(map, settings with { EnableConsole = false }, "test")), "repair cannot launch client without console");
    Reject(() => Hosting.DeferredClientArguments(Hosting.Build(map, settings with { Kind = HostKind.DedicatedIp }, "test")), "repair cannot restart a dedicated server");
    Check(plan.Boot.Contains("map_workshop 3070293560 de_fixture") && !plan.Arguments.Contains("-dedicated"), "listen launch uses actual workshop map name");
    Check(plan.Boot.IndexOf("lservercfgfile") < plan.Boot.IndexOf("map_workshop"), "post-load rule hook set before map load");
    Check(plan.Rules.Contains("bot_quota 3") && plan.Rules.Contains("bot_difficulty 2"), "bot rules present");
    var custom = Hosting.Build(map, settings with { ApplyRules = false }, "test");
    Check(!custom.Rules.Contains("bot_quota") && !custom.Rules.Contains("sv_cheats"), "custom map defaults preserved when rule overrides disabled");
    var dedicated = Hosting.Build(map, settings with { Kind = HostKind.DedicatedIp }, "test");
    Check(dedicated.Arguments.Contains("-dedicated") && dedicated.Boot.Contains("host_workshop_map 3070293560"), "dedicated uses server workshop command");
    Check(dedicated.Arguments.Contains("+map") && !dedicated.Arguments.Contains("+exec"), "dedicated workshop waits for initialization before boot request");
    Reject(() => Hosting.Build(map, settings with { Name = "bad\nquit" }, "test"), "cfg newline injection rejected");
    Reject(() => Hosting.Build(map, settings with { Password = "bad;quit" }, "test"), "password command injection rejected");
    Reject(() => Hosting.Build(map, settings with { Bots = 10 }, "test"), "reserve slot for host");
    Reject(() => Hosting.Build(map, settings with { Port = 70000 }, "test"), "invalid port rejected");
    Reject(() => Hosting.Build(map, settings with { Kind = HostKind.DedicatedP2P, Token = new string('a', 32) }, "test"), "P2P GSLT conflict rejected");
    Reject(() => Hosting.Build(new MapEntry { Id = map.Id }, settings, "test"), "undownloaded listen map rejected");
    Check(Hosting.JoinCommand("192.168.1.20", 27015, "hello") == "password \"hello\"; connect 192.168.1.20:27015", "IPv4 and password join command");
    Check(Hosting.JoinCommand("::1", 27015, "") == "connect [::1]:27015", "IPv6 join command");
    Check(Hosting.JoinCommand("[A:1:123:456]", 27015, "") == "connect [A:1:123:456]", "server P2P identity join command");
    Reject(() => Hosting.JoinCommand("127.0.0.1;quit", 27015, ""), "join address injection rejected");
    Check(Hosting.ParseServerIdentity("player [A:1:999:999]\nsteamid : [A:1:123:456] (12345)") == "[A:1:123:456]", "P2P parser accepts only server identity line");
    Check(Hosting.ParseServerIdentity("player [A:1:999:999]") == null, "player identity not mistaken for server");
    Check(Hosting.ParseServerIdentity("steamid : [A:1:0:0]") == null, "empty Steam identity is not shareable");
    Reject(() => Hosting.JoinCommand("[A:1:0:0]", 27015, ""), "empty P2P join command rejected");

    var original = "\"GameInfo\"\r\n{\r\n // Valve { keep comments }\r\n FileSystem { SteamAppId 730 ForceFixedAppIds 1 }\r\n ConVars { \"cl_usesocketsforloopback\" \"0\" }\r\n}\r\n";
    var patched = RelayPatch.PatchText(original);
    Check(patched.Contains("// Valve { keep comments }") && patched.Contains("ForceFixedAppIds 1") && patched.Contains("\"cl_usesocketsforloopback\" \"0\""), "relay patch preserves Valve fields and comments");
    Check(RelayPatch.PatchText(patched) == patched, "relay patch idempotent");
    var gameDir = Path.Combine(appRoot, "game", "csgo"); Directory.CreateDirectory(gameDir); var info = Path.Combine(gameDir, "gameinfo_branchspecific.gi");
    File.WriteAllText(info, original); RelayPatch.Enable(appRoot); Check(RelayPatch.Enabled(appRoot), "relay enabled verified through parser");
    RelayPatch.Restore(appRoot); Check(File.ReadAllText(info) == original, "relay byte content restored");
    RelayPatch.Enable(appRoot); File.AppendAllText(info, "// Steam update"); Reject(() => RelayPatch.Restore(appRoot), "restore refuses stale backup after external edit");

    if (args.Contains("--live"))
    {
        var root = args.SkipWhile(s => s != "--live").Skip(1).First();
        var detected = Discovery.Detect(root); var clock = Stopwatch.StartNew(); var actual = Discovery.Scan(detected);
        Console.WriteLine(JsonSerializer.Serialize(new { detected.Root, maps = actual.Maps.Count, workshop = actual.Maps.Count(m => !m.Official), elapsedMs = clock.ElapsedMilliseconds, warnings = actual.Warnings }, new JsonSerializerOptions { WriteIndented = true }));
        Check(actual.Maps.Any(m => !m.Official && m.MapName.Length > 0), "real installed workshop VPK map discovery");
        var local = actual.Maps.First(m => !m.Official);
        Console.WriteLine("Real map launch preview:\n" + Hosting.Build(local, settings, "test").Boot);
    }
    if (args.Contains("--network"))
    {
        var item = (await new Workshop().Fetch(new[] { "3070293560" })).Single();
        Check(item.IsMap && item.Title.Length > 0, "live Steam metadata validates CS2 map");
        Console.WriteLine("Steam metadata: " + item.Title + " | " + item.Tags);
    }
    try { RoomStatusParser.Parse("""{"server":{"map":"<empty>","clients_human":1,"clients_bot":0}}"""); throw new Exception("Empty map accepted"); }
    catch (InvalidOperationException) { Check(true, "empty map sentinel is not a loaded room"); }
    var switchPlan = RoomTransitions.ForExistingRoom(map, settings, "transition_test");
    Check(switchPlan.Boot.Contains($"changelevel {map.MapName} customgamemode={map.Id} nomapvalidation=1") && !switchPlan.Boot.Contains("map_workshop"), "existing workshop room changes level without recreating session");
    foreach (var kind in new[] { HostKind.DedicatedIp, HostKind.DedicatedP2P })
    {
        var dedicatedSwitch = RoomTransitions.ForExistingRoom(map, settings with { Kind = kind }, "dedicated_transition");
        Check(dedicatedSwitch.Boot.TrimEnd().EndsWith($"host_workshop_map {map.Id}") && !dedicatedSwitch.Boot.Contains("changelevel "), "dedicated workshop transition prepares SteamUGC cache before loading: " + kind);
    }
    var officialTarget = new MapEntry { Official = true, MapName = "de_dust2", FilePath = "fixture.vpk" };
    Check(RoomTransitions.ForExistingRoom(officialTarget, settings, "transition_official").Boot.Contains("changelevel de_dust2 customgamemode=0"), "returning to official map clears previous workshop addon");
    var roomBefore = new RoomStatus(map.MapName, 1, 0, "[A:1:123:456]", 27015, map.Id);
    Check(RoomTransitions.SameEndpoint(roomBefore, roomBefore with { Map = "de_dust2", Addon = "" }), "map change can preserve room identity and port");
    Check(!RoomTransitions.SameEndpoint(roomBefore, roomBefore with { Identity = "[A:1:124:456]" }) && !RoomTransitions.SameEndpoint(roomBefore, roomBefore with { Port = 27016 }), "identity or port change invalidates old endpoint");
    Check(!RoomTransitions.Matches(roomBefore with { Addon = "999" }, map), "same filename from wrong workshop addon is not success");
    Check(!RoomTransitions.Matches(roomBefore with { Map = "de_dust2" }, officialTarget), "official target with old workshop addon is not success");
    Check(RoomTransitions.ReconnectCommand("10.126.126.5", 27015, "") == "disconnect; connect 10.126.126.5:27015", "reconnect command only disconnects executing friend");
    Reject(() => RoomTransitions.ForExistingRoom(new MapEntry { Id = "123", ValidatedRemote = true }, settings with { Kind = HostKind.DedicatedIp }, "remote_switch"), "preserving room requires downloaded map");
    Check(!RoomTransitions.SameEndpoint(roomBefore with { Identity = "[A:1:0:0]" }, roomBefore with { Identity = "[A:1:0:0]" }), "invalid Steam identity cannot prove endpoint continuity");
    Check(RoomTransitions.ConsoleCommands(switchPlan.Boot).Contains("changelevel ") && !RoomTransitions.ConsoleCommands(switchPlan.Boot).Contains("exec "), "live switching bypasses restricted workshop cfg execution");
    Check(RoomTransitions.ConsoleCommands("// generated\r\nsv_lan 0\r\n\r\necho OK\n") == "sv_lan 0; echo OK", "generated config becomes one valid console command without comments or newlines");
    Console.WriteLine($"ALL {passed} CHECKS PASSED");
}
finally { Directory.Delete(temp, true); }
