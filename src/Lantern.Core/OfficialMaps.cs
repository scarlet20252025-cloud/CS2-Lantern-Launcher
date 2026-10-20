namespace Lantern.Core;
public static class OfficialMaps {
static readonly Dictionary<string,string> Names = new(StringComparer.OrdinalIgnoreCase) {
 ["ar_baggage"] = "行李仓库",
 ["ar_pool_day"] = "泳池派对",
 ["ar_shoots_night"] = "山林夜寨",
 ["ar_shoots"] = "山林小寨",
 ["cs_italy"] = "意大利小镇",
 ["cs_office"] = "办公室",
 ["cs_shelter"] = "动物收容所",
 ["de_ancient_night"] = "远古遗迹 · 夜间",
 ["de_ancient"] = "远古遗迹",
 ["de_anubis"] = "阿努比斯",
 ["de_boulder"] = "岩峰修道院",
 ["de_cache"] = "死城之谜",
 ["de_debris"] = "残翼小镇",
 ["de_dust2"] = "炙热沙城 II",
 ["de_eldorado"] = "黄金之城",
 ["de_fachwerk"] = "木筋屋小镇",
 ["de_inferno"] = "炼狱小镇",
 ["de_mirage"] = "荒漠迷城",
 ["de_nuke"] = "核子危机",
 ["de_overpass"] = "死亡游乐园",
 ["de_poseidon"] = "波塞冬",
 ["de_train"] = "列车停放站",
 ["de_vertigo"] = "殒命大厦",
};
public static string Name(string map) => Names.TryGetValue(map, out var name) ? name : map;
public static bool HasCover(string map) => Names.ContainsKey(map);
}
