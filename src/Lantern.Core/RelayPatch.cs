using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lantern.Core;

public static class RelayPatch
{
    public static string PatchText(string text)
    {
        // Keep Valve's entire file including comments and unknown blocks.
        text = SetInBlock(text, "ConVars", "net_p2p_listen_dedicated", "1");
        return SetInBlock(text, "NetworkSystem", "CreateListenSocketP2P", "2");
    }
    static string SetInBlock(string text, string block, string key, string value)
    {
        var tokens = Regex.Matches(text, "//[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|[{}]|[^\\s{}\"]+")
            .Cast<Match>().Where(m => !m.Value.StartsWith("//")).ToArray();
        int depth = 0, open = -1, close = -1, rootClose = -1;
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (depth == 1 && t.Value.Trim('"').Equals(block, StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length && tokens[i + 1].Value == "{")
            {
                open = i + 1; int d = 1;
                for (var j = open + 1; j < tokens.Length; j++) { if (tokens[j].Value == "{") d++;
                    if (tokens[j].Value == "}") d--; if (d == 0) { close = j; break; } }
                break;
            }
            if (t.Value == "{") depth++;
            if (t.Value == "}") { depth--; if (depth == 0) rootClose = t.Index; }
        }
        if (open < 0)
        {
            if (rootClose < 0) throw new InvalidDataException("gameinfo 根结构无效");
            return text.Insert(rootClose, $"\n\t{block}\n\t{{\n\t\t\"{key}\" \"{value}\"\n\t}}\n");
        }
        if (close < 0) throw new InvalidDataException("gameinfo block 未闭合");
        for (int i = open + 1; i + 1 < close; i++)
            if (tokens[i].Value.Trim('"').Equals(key, StringComparison.OrdinalIgnoreCase))
            { var v = tokens[i + 1]; if (v.Value is "{" or "}") throw new InvalidDataException("意外的 P2P 设置结构"); return text[..v.Index] + "\"" + value + "\"" + text[(v.Index + v.Length)..]; }
        return text.Insert(tokens[close].Index, $"\t\"{key}\" \"{value}\"\n\t");
    }
    static string FilePath(string root) => Path.Combine(root, "game", "csgo", "gameinfo_branchspecific.gi");
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static bool Enabled(string root)
    {
        var path = FilePath(root); if (!File.Exists(path)) return false;
        var game = Kv.Parse(File.ReadAllText(path)).Node("GameInfo");
        return game?.Node("ConVars")?.Get("net_p2p_listen_dedicated") == "1" && game.Node("NetworkSystem")?.Get("CreateListenSocketP2P") == "2";
    }
    public static void Enable(string root)
    {
        var path = FilePath(root); if (Enabled(root)) return;
        if (File.Exists(path + ".lantern-backup")) throw new IOException("已有 P2P 备份。请先恢复，避免覆盖原始配置。");
        var original = File.ReadAllBytes(path);
        var text = File.ReadAllText(path);
        if (Kv.Parse(text).Node("GameInfo")?.Node("FileSystem")?.Get("SteamAppId") != "730") throw new InvalidDataException("不是 CS2 branchspecific 配置，已取消修改。");
        var patched = new UTF8Encoding(false).GetBytes(PatchText(text));
        File.WriteAllBytes(path + ".lantern-backup", original);
        File.WriteAllText(path + ".lantern-hash", Hash(patched));
        File.WriteAllBytes(path + ".lantern-tmp", patched); File.Move(path + ".lantern-tmp", path, true);
    }
    public static void Restore(string root)
    {
        var path = FilePath(root);
        if (!File.Exists(path + ".lantern-backup")) throw new IOException("没有本启动器创建的备份。");
        if (!File.Exists(path + ".lantern-hash") || Hash(File.ReadAllBytes(path)) != File.ReadAllText(path + ".lantern-hash"))
            throw new IOException("文件已被游戏更新或其他程序改动，不能直接恢复旧版本。备份已保留，请通过 Steam 验证文件或人工合并。");
        File.WriteAllBytes(path + ".lantern-tmp", File.ReadAllBytes(path + ".lantern-backup")); File.Move(path + ".lantern-tmp", path, true);
        File.Delete(path + ".lantern-backup"); File.Delete(path + ".lantern-hash");
    }
}
