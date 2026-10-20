namespace Lantern.Core;

public static class MapCatalog
{
    public static string PackageKey(MapEntry map) => map.Official ? map.Identity : "workshop:" + map.Id;
    public static List<MapEntry> Cards(IEnumerable<MapEntry> maps, string selected = "") => maps
        .GroupBy(PackageKey)
        .Select(group => group.OrderByDescending(m => m.Identity == selected)
            .ThenByDescending(m => m.Local)
            .ThenBy(m => m.MapName.Contains("prefab", StringComparison.OrdinalIgnoreCase) || m.MapName.Contains("test", StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.MapName.Length).ThenBy(m => m.MapName, StringComparer.OrdinalIgnoreCase).First()).ToList();
}
