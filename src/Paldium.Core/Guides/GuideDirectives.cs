using System.Text.RegularExpressions;

namespace Paldium.Core.Guides;

public enum GuideDirectiveKind { Figure, Try }

/// <summary>
/// A block directive inside a guide: <c>[[figure:power-line a=Alpaca b=Anubis]]</c> or
/// <c>[[try:calculator mode=cift a=Alpaca b=Anubis label="Open this pair"]]</c>.
/// </summary>
public sealed record GuideDirective(GuideDirectiveKind Kind, string Name, IReadOnlyDictionary<string, string> Args)
{
    public string Arg(string key, string fallback = "") => Args.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>Link target of a <c>try</c> directive: the page name and every argument except <c>label</c> as a query string.</summary>
    public string TryHref()
    {
        var query = string.Join("&", Args.Where(p => p.Key != "label").Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return query.Length == 0 ? Name : $"{Name}?{query}";
    }
}

public static partial class GuideDirectives
{
    [GeneratedRegex(@"\[\[(figure|try):([A-Za-z][A-Za-z0-9\-]*)((?:\s+[A-Za-z]+=(?:""[^""]*""|[^\s\]]+))*)\s*\]\]")]
    private static partial Regex DirectivePattern();

    [GeneratedRegex(@"([A-Za-z]+)=(?:""([^""]*)""|([^\s\]]+))")]
    private static partial Regex ArgPattern();

    /// <summary>Replaces every directive with the text the callback returns (it gets the directive and its index in the document).</summary>
    public static string Replace(string markdown, Func<GuideDirective, int, string> replacement)
    {
        var index = 0;
        return DirectivePattern().Replace(markdown, m => replacement(Parse(m), index++));
    }

    public static IReadOnlyList<GuideDirective> Find(string markdown) =>
        DirectivePattern().Matches(markdown).Select(Parse).ToList();

    private static GuideDirective Parse(Match m)
    {
        var args = ArgPattern().Matches(m.Groups[3].Value)
            .ToDictionary(a => a.Groups[1].Value, a => a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value, StringComparer.Ordinal);
        return new GuideDirective(m.Groups[1].Value == "figure" ? GuideDirectiveKind.Figure : GuideDirectiveKind.Try, m.Groups[2].Value, args);
    }
}
