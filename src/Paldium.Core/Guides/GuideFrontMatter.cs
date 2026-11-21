using System.Text.RegularExpressions;

namespace Paldium.Core.Guides;

/// <summary>A related tool of a guide ("Calculator · Pair to child"), shown on the library card and in the reader side column.</summary>
public sealed record GuideTool(string Label, string Href);

/// <summary>The YAML front matter of a guide file (screens/guides.md): group, summary, tools, card art and the date of a meta guide.</summary>
public sealed record GuideMeta(string Group, string Summary, IReadOnlyList<GuideTool> Tools, string Art, string? Dated)
{
    public static readonly GuideMeta Empty = new("", "", [], "", null);
}

/// <summary>Splits a guide file into its front matter and its markdown body. Only the small YAML subset the guides use is read.</summary>
public static partial class GuideFrontMatter
{
    [GeneratedRegex(@"(\w+):\s*(?:""([^""]*)""|([^,}]+))")]
    private static partial Regex InlinePair();

    /// <summary>Words per minute used for the reading time.</summary>
    public const int WordsPerMinute = 200;

    public static (GuideMeta Meta, string Body) Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (GuideMeta.Empty, normalized);
        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (GuideMeta.Empty, normalized);

        string group = "", summary = "", art = "";
        string? dated = null;
        var tools = new List<GuideTool>();
        var inTools = false;
        foreach (var raw in normalized[4..end].Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (inTools && line.TrimStart().StartsWith('-'))
            {
                var pairs = InlinePair().Matches(line).ToDictionary(m => m.Groups[1].Value, m => (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim());
                if (pairs.TryGetValue("label", out var label) && pairs.TryGetValue("href", out var href)) tools.Add(new GuideTool(label, href));
                continue;
            }

            inTools = false;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "group": group = value; break;
                case "summary": summary = value; break;
                case "art": art = value; break;
                case "dated": dated = value.Length == 0 ? null : value; break;
                case "tools": inTools = true; break;
            }
        }

        var body = normalized[(end + 4)..].TrimStart('\n');
        return (new GuideMeta(group, summary, tools, art, dated), body);
    }

    /// <summary>Reading time in minutes (200 words a minute, rounded up, at least one).</summary>
    public static int ReadingMinutes(string markdown)
    {
        var words = WordPattern().Matches(markdown).Count;
        return Math.Max(1, (words + WordsPerMinute - 1) / WordsPerMinute);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
