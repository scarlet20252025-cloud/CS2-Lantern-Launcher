using System.Text.RegularExpressions;

namespace Lantern.Core;

// Steam's text VDF format; duplicate keys are retained (e.g. SearchPaths).
public sealed class Kv
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public List<Kv> Children { get; } = new();
    public Kv? Node(string key) => Children.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    public string Get(string key, string fallback = "") => Node(key)?.Value ?? fallback;
    public Kv Block(string key) { var n = Node(key); if (n != null) return n; n = new Kv { Key = key }; Children.Add(n); return n; }
    public void Set(string key, string value) { var n = Block(key); n.Value = value; }

    public static Kv Parse(string source)
    {
        var tokens = Regex.Matches(source, "//[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|[{}]|[^\\s{}\"]+")
            .Select(m => m.Value).Where(s => !s.StartsWith("//")).ToArray();
        int i = 0;
        string Decode(string s) => s.StartsWith('"') ? s[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : s;
        void Read(Kv node, bool nested)
        {
            while (i < tokens.Length)
            {
                if (tokens[i] == "}") { if (!nested) throw new InvalidDataException("Unexpected VDF brace"); i++; return; }
                var key = Decode(tokens[i++]);
                if (i >= tokens.Length) throw new InvalidDataException("Incomplete VDF value");
                var item = new Kv { Key = key }; node.Children.Add(item);
                if (tokens[i] == "{") { i++; Read(item, true); }
                else item.Value = Decode(tokens[i++]);
            }
            if (nested) throw new InvalidDataException("Unclosed VDF block");
        }
        var root = new Kv(); Read(root, false); return root;
    }
}
