namespace Lantern.Core;

public static class RoomTransitions
{
    public static string ConsoleCommands(string generatedConfig) => string.Join("; ", generatedConfig.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("//")));

    public static LaunchPlan ForExistingRoom(MapEntry map, HostSettings settings, string session)
    {
        var plan = Hosting.Build(map, settings, session);
        if (!map.Local) throw new InvalidOperationException("保留房间换图需要地图已下载到本地，请先完成下载。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(map.MapName, @"^[a-zA-Z0-9_\-]+$"))
            throw new ArgumentException("地图内部名称无效。");
        var lines = plan.Boot.TrimEnd().Split('\n');
        // Dedicated servers maintain a separate SteamUGC cache. A bare changelevel
        // can select an addon before it is downloaded/mounted and terminate the server.
        // host_workshop_map resolves that cache and performs an in-process changelevel.
        lines[^1] = !map.Official && settings.Kind != HostKind.Listen
            ? $"host_workshop_map {map.Id}"
            : $"changelevel {map.MapName} customgamemode={(map.Official ? "0" : map.Id)} nomapvalidation=1";
        return plan with { Boot = string.Join('\n', lines) + "\n" };
    }
    public static bool Matches(RoomStatus room, MapEntry map) => room.Map == map.MapName &&
        (map.Official ? string.IsNullOrEmpty(room.Addon) || room.Addon == "0" : room.Addon == map.Id);

    public static bool SameEndpoint(RoomStatus before, RoomStatus after) =>
        before.Port == after.Port && Hosting.ParseServerIdentity("steamid : " + before.Identity) != null && before.Identity == after.Identity;

    public static string ReconnectCommand(string address, int port) =>
        "disconnect; " + Hosting.JoinCommand(address, port);
}
