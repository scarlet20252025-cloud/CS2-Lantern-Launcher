using System.Diagnostics;
using System.Text.Json;
using Lantern.Core;

var root = @"E:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive";
var key = File.ReadAllText(Environment.GetEnvironmentVariable("LANTERN_PROBE_KEY_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2Lantern", "console-key.txt")).Trim();
var output = Path.GetFullPath("artifacts/relay-trial");
Directory.CreateDirectory(output);
var mode = args.FirstOrDefault() ?? "status";
if (mode == "map-names")
{
    Console.WriteLine(JsonSerializer.Serialize(Vpk.ReadMapNames(args[1])));
    return;
}
if (mode == "start")
{
    if (Process.GetProcessesByName("cs2").Any(p => { try { return p.MainWindowTitle.Contains("Dedicated"); } catch { return false; } }))
        throw new InvalidOperationException("An existing dedicated server must be inspected first.");
    RelayPatch.Enable(root);
    var settings = new HostSettings { Kind = HostKind.DedicatedP2P, Port = 27016, MaxPlayers = 32, ApplyRules = true, Bots = 0, ConsolePassword = key, Name = "Lantern SDR Trial" };
    var plan = Hosting.Build(new MapEntry { Official = true, MapName = "de_dust2", FilePath = Path.Combine(root,"game","csgo","maps","de_dust2.vpk") }, settings, "sdr_trial");
    Hosting.WriteConfigs(root, plan);
    var start = new ProcessStartInfo(Path.Combine(root, "game", "bin", "win64", "cs2.exe")) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    foreach (var arg in plan.Arguments) start.ArgumentList.Add(arg);
    var process = Process.Start(start)!;
    File.WriteAllText(Path.Combine(output, "server-pid.txt"), process.Id.ToString());
    Console.WriteLine("Started dedicated server PID " + process.Id + ", UDP 27016, NetCon 2122");
    return;
}
int port = args.Length > 1 ? int.Parse(args[1]) : 2122;
using var console = new GameConsole();
using var log = new StreamWriter(Path.Combine(output, $"{DateTime.Now:yyyyMMdd-HHmmss}-{port}.log")) { AutoFlush = true };
console.Output += line => { lock(log) log.Write(line); };
await console.Connect(port, key);
if (mode == "cycle")
{
    bool fullyJoined = false;
    console.Output += line => { if (line.Contains("-> SIGNONSTATE_FULL")) fullyJoined = true; };
    using var client = new GameConsole();
    client.Output += line => { lock(log) log.Write("CLIENT " + line); };
    await client.Connect(2121, key);
    var initial = await console.ReadStatus();
    await client.Send(Hosting.JoinCommand(initial.Identity, initial.Port, ""));
    async Task Verify(string name, string addon)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90); int stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            RoomStatus current;
            try { current = await console.ReadStatus(); }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException) { stable = 0; continue; }
            bool matches = current.Map == name && (addon.Length == 0 ? current.Addon is "" or "0" : current.Addon == addon);
            stable = matches && fullyJoined && current.Humans == 1 && RoomTransitions.SameEndpoint(initial, current) ? stable + 1 : 0;
            if (stable >= 3) { Console.WriteLine("PASS " + JsonSerializer.Serialize(current)); return; }
        }
        throw new Exception("Map/client/identity verification timed out: " + name);
    }
    await Verify(initial.Map, initial.Addon);
    foreach (var (name, addon) in new[] { ("crazy_chickens", "3815559839"), ("de_dust2", ""), ("crazy_chickens", "3815559839") })
    {
        var map = new MapEntry { Official = addon.Length == 0, Id = addon, MapName = name, FilePath = "verified-local" };
        var plan = RoomTransitions.ForExistingRoom(map, new HostSettings { Kind = HostKind.DedicatedP2P, ConsolePassword = key, Port = 27016, MaxPlayers = 32 }, "sdr_cycle");
        Hosting.WriteConfigs(root, plan);
        fullyJoined = false;
        await console.Send(RoomTransitions.ConsoleCommands(plan.Boot));
        await Task.Delay(3000);
        await Verify(name, addon);
    }
    fullyJoined = false;
    await client.Send("disconnect"); await Task.Delay(2000);
    await client.Send(Hosting.JoinCommand(initial.Identity, initial.Port, ""));
    await Verify("crazy_chickens", "3815559839");
    Console.WriteLine("PASS reconnect using original identity");
    await client.Send("net_connections_stats"); await Task.Delay(1000);
}
else if (mode == "add-team")
{
    Console.WriteLine(JsonSerializer.Serialize(await TeamBots.Add(console, args[2], int.Parse(args[3]), 32)));
}
else if (mode == "command")
{
    await console.Send(args[2]);
    await Task.Delay(args.Length > 3 ? int.Parse(args[3]) * 1000 : 3000);
}
else if (mode == "wait")
{
    await Task.Delay(args.Length > 2 ? int.Parse(args[2]) * 1000 : 15000);
}
else if (mode == "players") Console.WriteLine("Active humans: " + await console.ReadActiveHumans());
try { Console.WriteLine(JsonSerializer.Serialize(await console.ReadStatus())); }
catch (InvalidOperationException e) { Console.WriteLine(e.Message); }
