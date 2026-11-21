using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.Extensions.Logging;
using Paldium.App.Components;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Guides;
using Paldium.Core.Queries;

namespace Paldium.App.Services;

/// <summary>A guide as shown on a library card.</summary>
public sealed record GuideInfo(string Slug, string Title, string Group, string Summary, IReadOnlyList<GuideTool> Tools, string Art, string? Dated, int Minutes);

/// <summary>A heading of a guide ("On this page").</summary>
public sealed record GuideHeading(string Id, string Text);

/// <summary>A part of the guide body: rendered HTML, or a figure or a "try it" button the reader draws with a component.</summary>
public abstract record GuideBlock;

public sealed record GuideHtmlBlock(string Html) : GuideBlock;

public sealed record GuideDirectiveBlock(GuideDirective Directive) : GuideBlock;

/// <summary>A guide ready to read.</summary>
/// <param name="FellBack">The guide does not exist in the asked language; the English one is shown.</param>
public sealed record GuideDoc(GuideInfo Info, IReadOnlyList<GuideBlock> Blocks, IReadOnlyList<GuideHeading> Outline, IReadOnlyList<string> PalIds, bool FellBack);

/// <summary>Loads the guide markdown files (selected language, English as the fallback), reads their front matter and renders them.</summary>
public sealed partial class GuideService(IGameData data, string contentFolder, ILogger<GuideService> log)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private readonly Dictionary<(string Slug, string Lang), (GuideInfo Info, string Body, string Folded)> _cache = [];

    [GeneratedRegex(@"<p><!--pbblock:(\d+)--></p>|<!--pbblock:(\d+)-->")]
    private static partial Regex BlockMarker();

    [GeneratedRegex(@"<h2 id=""([^""]*)"">(.*?)</h2>", RegexOptions.Singleline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    public IReadOnlyList<GuideInfo> List(string lang) =>
        Slugs().Select(slug => Entry(slug, lang)?.Info).OfType<GuideInfo>().ToList();

    /// <summary>The guides whose title, summary or text contains every word of the query (accent- and case-insensitive).</summary>
    public IReadOnlyList<GuideInfo> Search(string lang, string? query)
    {
        var all = List(lang);
        var words = TextFold.Fold(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return all;
        return all.Where(g => Entry(g.Slug, lang) is { } e && words.All(w => e.Folded.Contains(w, StringComparison.Ordinal))).ToList();
    }

    /// <summary>The guide, or null when it does not exist in any language.</summary>
    public GuideDoc? Load(string slug, string lang)
    {
        var entry = Entry(slug, lang);
        if (entry == null) return null;
        var fellBack = lang != "en" && !File.Exists(Path.Combine(contentFolder, lang, slug + ".md"));

        var pals = new List<string>();
        var markdown = GuideLinkParser.Replace(entry.Value.Body, link => Resolve(link, lang, pals), message => log.LogWarning("{Message} in guide {Guide}", message, slug));
        var directives = new List<GuideDirective>();
        markdown = GuideDirectives.Replace(markdown, (d, i) =>
        {
            directives.Add(d);
            return $"\n\n<!--pbblock:{i}-->\n\n";
        });

        // The title is shown by the reader itself; the first paragraph is the lede.
        var lines = markdown.Split('\n').ToList();
        var h1 = lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal));
        if (h1 >= 0) lines.RemoveAt(h1);
        var html = Markdown.ToHtml(string.Join('\n', lines).TrimStart('\n'), Pipeline);
        html = StyleLists(html);
        var firstParagraph = html.IndexOf("<p>", StringComparison.Ordinal);
        if (firstParagraph == 0) html = "<p class=\"lede\">" + html[3..];

        var outline = HeadingPattern().Matches(html).Select(m => new GuideHeading(m.Groups[1].Value, WebUtility.HtmlDecode(TagPattern().Replace(m.Groups[2].Value, "")))).ToList();

        var blocks = new List<GuideBlock>();
        var position = 0;
        foreach (Match m in BlockMarker().Matches(html))
        {
            if (m.Index > position) blocks.Add(new GuideHtmlBlock(html[position..m.Index]));
            var index = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture);
            blocks.Add(new GuideDirectiveBlock(directives[index]));
            position = m.Index + m.Length;
        }

        if (position < html.Length) blocks.Add(new GuideHtmlBlock(html[position..]));
        return new GuideDoc(entry.Value.Info, blocks, outline, pals.Distinct().ToList(), fellBack);
    }

    // Numbered steps get a number circle (the item text goes in a span); bullet lists get the dot style.
    private static string StyleLists(string html)
    {
        html = html.Replace("<ul>", "<ul class=\"dots2\">", StringComparison.Ordinal);
        return Regex.Replace(html, "<ol>(.*?)</ol>", m =>
            "<ol class=\"steps2\">" + m.Groups[1].Value.Replace("<li>", "<li><span>", StringComparison.Ordinal).Replace("</li>", "</span></li>", StringComparison.Ordinal) + "</ol>",
            RegexOptions.Singleline);
    }

    private (GuideInfo Info, string Body, string Folded)? Entry(string slug, string lang)
    {
        if (_cache.TryGetValue((slug, lang), out var cached)) return cached;
        var path = FindFile(slug, lang);
        if (path == null) return null;
        var (meta, body) = GuideFrontMatter.Parse(File.ReadAllText(path));
        var title = body.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? slug;
        var info = new GuideInfo(slug, title, meta.Group, meta.Summary, meta.Tools, meta.Art, meta.Dated, GuideFrontMatter.ReadingMinutes(body));
        var entry = (info, body, TextFold.Fold(title + " " + meta.Summary + " " + body));
        _cache[(slug, lang)] = entry;
        return entry;
    }

    private IEnumerable<string> Slugs()
    {
        var en = Path.Combine(contentFolder, "en");
        if (!Directory.Exists(en)) return [];
        return Directory.EnumerateFiles(en, "*.md").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal);
    }

    private string? FindFile(string slug, string lang)
    {
        var chosen = Path.Combine(contentFolder, lang, slug + ".md");
        if (File.Exists(chosen)) return chosen;
        var fallback = Path.Combine(contentFolder, "en", slug + ".md");
        return File.Exists(fallback) ? fallback : null;
    }

    // Inline references (screens/guides.md): a Pal is its icon and a bold name linking to the Paldex; a passive is its rank glyph and name linking to Skills.
    private string? Resolve(GuideLink link, string lang, List<string> pals)
    {
        try
        {
            if (link.Kind == GuideLinkKind.Pal)
            {
                var pal = data.Pal(link.Id);
                pals.Add(pal.Id);
                return $"<a class=\"palref\" href=\"paldex?pal={WebUtility.UrlEncode(pal.Id)}\"><img class=\"pal\" src=\"gamedata/{WebUtility.HtmlEncode(pal.IconPath)}\" width=\"22\" height=\"22\" alt=\"\"><b>{WebUtility.HtmlEncode(pal.DisplayName(lang))}</b></a>";
            }

            var passive = data.Passive(link.Id);
            var rank = passive.Rank < 0
                ? $"Passive_Negative_{Math.Clamp(-passive.Rank, 1, 3)}"
                : $"Passive_Positive_{Math.Clamp(passive.Rank, 1, 5)}";
            return $"<a class=\"palref\" href=\"skills?passive={WebUtility.UrlEncode(passive.Id)}\"><img class=\"rank\" src=\"gamedata/icons/passive-rank/{rank}_icon.png\" width=\"16\" height=\"16\" alt=\"\"><b>{WebUtility.HtmlEncode(passive.Names.Get(lang))}</b></a>";
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}
