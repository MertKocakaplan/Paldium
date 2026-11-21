using System.Text.RegularExpressions;

namespace Paldium.Core.Guides;

/// <summary>Kind of a link inside a guide: <c>[[pal:Id]]</c> or <c>[[passive:Id]]</c>.</summary>
public enum GuideLinkKind { Pal, Passive }

public sealed record GuideLink(GuideLinkKind Kind, string Id);

/// <summary>Finds the <c>[[pal:Id]]</c> / <c>[[passive:Id]]</c> links in guide markdown and replaces them with markup.</summary>
public static partial class GuideLinkParser
{
    [GeneratedRegex(@"\[\[(pal|passive):([A-Za-z0-9_\-]+)\]\]")]
    private static partial Regex LinkPattern();

    public static IReadOnlyList<GuideLink> Find(string markdown) =>
        LinkPattern().Matches(markdown).Select(m => ToLink(m)).ToList();

    /// <summary>
    /// Replaces every link with the string the resolver returns. A resolver result of null means "unknown id": the id is left as
    /// plain text and <paramref name="warn"/> is called with a message.
    /// </summary>
    public static string Replace(string markdown, Func<GuideLink, string?> resolver, Action<string>? warn = null) =>
        LinkPattern().Replace(markdown, m =>
        {
            var link = ToLink(m);
            var html = resolver(link);
            if (html != null) return html;
            warn?.Invoke($"Guide link to unknown {link.Kind.ToString().ToLowerInvariant()} '{link.Id}'");
            return link.Id;
        });

    private static GuideLink ToLink(Match m) =>
        new(m.Groups[1].Value == "pal" ? GuideLinkKind.Pal : GuideLinkKind.Passive, m.Groups[2].Value);
}
