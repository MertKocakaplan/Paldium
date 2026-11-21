using Paldium.Core.Data;
using Paldium.Core.Guides;

namespace Paldium.Core.Tests;

public class GuideContentTests
{
    private static readonly string Folder = Path.Combine(TestPaths.RepoRoot, "content", "guides");

    [Fact]
    public void SevenGuides_ExistInEnglishAndTurkish()
    {
        var en = Directory.GetFiles(Path.Combine(Folder, "en"), "*.md").Select(Path.GetFileName).Order().ToList();
        var tr = Directory.GetFiles(Path.Combine(Folder, "tr"), "*.md").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(7, en.Count);
        Assert.Equal(en, tr);
    }

    [Fact]
    public void EveryLinkInEveryGuide_PointsToAKnownPalOrPassive()
    {
        var data = GameDataLoader.Load(TestPaths.DataFolder);
        foreach (var file in Directory.GetFiles(Folder, "*.md", SearchOption.AllDirectories))
            foreach (var link in GuideLinkParser.Find(File.ReadAllText(file)))
            {
                var known = link.Kind == GuideLinkKind.Pal
                    ? data.Pals.Any(p => p.Id == link.Id)
                    : data.Passives.Any(p => p.Id == link.Id);
                Assert.True(known, $"{Path.GetFileName(file)}: unknown {link.Kind} '{link.Id}'");
            }
    }
}
