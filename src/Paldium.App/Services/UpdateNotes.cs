using System.Text.RegularExpressions;
using Markdig;

namespace Paldium.App.Services;

/// <summary>
/// The release text in the update dialog: headings, lists and bold only. HTML in the text is shown as text (not run), links stay plain text and images are dropped.
/// </summary>
public static partial class UpdateNotes
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    [GeneratedRegex(@"</?a\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTag();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTag();

    public static string ToHtml(string markdown)
    {
        var html = Markdown.ToHtml(markdown ?? "", Pipeline);
        return AnchorTag().Replace(ImageTag().Replace(html, ""), "");
    }
}
