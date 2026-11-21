using Paldium.App.Pages.PlannerParts;
using Paldium.Core.Domain;
using Paldium.Core.Planning;

namespace Paldium.App.Tests;

public class PlanTreeLayoutTests
{
    private static OwnedPal Pal(string uid, string species, Gender gender, params string[] passives) =>
        new(uid, null, species, gender, 10, 1, passives, null, [], [], null, null,
            new PalLocation(LocationKind.Palbox, null, 14, null, 1),
            PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    private static BreedStep Step(PlanNode a, PlanNode b, string child, params string[] passives) =>
        new(a, b, child, GenderReq.Any, passives, 0, null, 0.3, 3.2);

    [Fact]
    public void TwoStepTree_PlacesNodesOnSixteenPxGrid_WithCurrentStepHighlighted()
    {
        var leafA = new OwnedLeaf(Pal("a", "KingBahamut", Gender.Male, "CraftMaster", "Artisan", "WorkSlave"));
        var leafB = new OwnedLeaf(Pal("b", "VolcanicMonster_Ice", Gender.Female, "Serious", "Nocturnal"));
        var mid = Step(leafA, leafB, "GrassGolem", "CraftMaster", "Artisan", "Serious");
        var leafC = new OwnedLeaf(Pal("c", "BlueThunderHorse", Gender.Male, "HeartOfTheImmovableKing", "Coward"));
        var root = Step(mid, leafC, "Anubis", "CraftMaster", "Artisan", "Serious", "HeartOfTheImmovableKing");
        var plan = new BreedingPlan(root, 2, 9.0, 0, false, [], []);

        var layout = PlanTreeLayout.Build(plan, currentStepIndex: 0);

        Assert.Equal(4, layout.Nodes.Count);
        Assert.Equal(2, layout.Joins.Count);
        Assert.Equal(6, layout.Edges.Count);
        Assert.Equal(680, layout.TargetX);
        Assert.Equal(0, (layout.TargetX - 8) % 16);
        Assert.Equal(0, layout.TargetY % 16);

        foreach (var node in layout.Nodes)
        {
            Assert.Equal(0, (node.X - 8) % 16);
            Assert.Equal(0, node.Y % 16);
        }

        Assert.Equal(2, layout.Nodes.Count(n => n.Current));
        Assert.Equal(1, layout.Joins.Count(j => j.Current));
        Assert.Equal(3, layout.Edges.Count(e => e.Current));

        var layoutStep2 = PlanTreeLayout.Build(plan, currentStepIndex: 1);
        Assert.Equal(2, layoutStep2.Nodes.Count(n => n.Current));
        Assert.All(layoutStep2.Nodes.Where(n => n.X == 360), n => Assert.True(n.Current));
        Assert.True(layoutStep2.Joins[1].Current);
    }

    [Fact]
    public void SymmetricFourLeafTree_DoesNotOverlapNodesInSameColumn()
    {
        var l1 = new OwnedLeaf(Pal("1", "SheepBall", Gender.Male, "Lucky"));
        var l2 = new OwnedLeaf(Pal("2", "PinkCat", Gender.Female, "Artisan"));
        var l3 = new OwnedLeaf(Pal("3", "Foxcicle", Gender.Male, "Serious"));
        var l4 = new OwnedLeaf(Pal("4", "Pengullet", Gender.Female, "Workaholic"));
        var s1 = Step(l1, l2, "Cattiva", "Lucky", "Artisan");
        var s2 = Step(l3, l4, "Foxparks", "Serious", "Workaholic");
        var root = Step(s1, s2, "Anubis", "Lucky", "Artisan", "Serious", "Workaholic");
        var plan = new BreedingPlan(root, 3, 12.0, 0, false, [], []);

        var layout = PlanTreeLayout.Build(plan, currentStepIndex: 0);

        foreach (var colGroup in layout.Nodes.GroupBy(n => n.X))
        {
            var ordered = colGroup.OrderBy(n => n.Y).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i].Y - ordered[i - 1].Y >= 96, $"Nodes in column X={colGroup.Key} overlapped: {ordered[i - 1].Y} vs {ordered[i].Y}");
        }
    }

    [Fact]
    public void GoneLeaf_MarksNodeGone_AndOutgoingEdgeGhost()
    {
        var leafA = new OwnedLeaf(Pal("a", "KingBahamut", Gender.Male, "CraftMaster"));
        var leafB = new OwnedLeaf(Pal("b", "VolcanicMonster_Ice", Gender.Female, "Serious"));
        var mid = Step(leafA, leafB, "GrassGolem", "CraftMaster", "Serious");
        var leafC = new OwnedLeaf(Pal("gone-c", "BlueThunderHorse", Gender.Male, "HeartOfTheImmovableKing"));
        var root = Step(mid, leafC, "Anubis", "CraftMaster", "Serious", "HeartOfTheImmovableKing");
        var plan = new BreedingPlan(root, 2, 9.0, 0, false, [], []);

        var layout = PlanTreeLayout.Build(plan, currentStepIndex: 0, goneUids: new HashSet<string> { "gone-c" });

        var goneNode = Assert.Single(layout.Nodes, n => n.Gone);
        Assert.Equal("BlueThunderHorse", goneNode.Node.SpeciesId);
        Assert.Single(layout.Edges, e => e.Ghost);
    }

    [Fact]
    public void TheLayout_IsInPlainPixels_NotScaledByTheWindow()
    {
        var leafA = new OwnedLeaf(Pal("a", "KingBahamut", Gender.Male, "CraftMaster"));
        var leafB = new OwnedLeaf(Pal("b", "VolcanicMonster_Ice", Gender.Female, "Serious"));
        var plan = new BreedingPlan(Step(leafA, leafB, "Anubis", "CraftMaster"), 1, 3.0, 0, false, [], []);

        var one = PlanTreeLayout.Build(plan, 0);
        var two = PlanTreeLayout.Build(plan, 0);
        // The same tree always gives the same pixels (nodes, joins and edges share one coordinate system).
        Assert.Equal(one.Nodes.Select(n => (n.X, n.Y)), two.Nodes.Select(n => (n.X, n.Y)));
        Assert.Equal(one.Edges.Select(e => e.D), two.Edges.Select(e => e.D));
        // The egg sits between the parents' right edge and the target column.
        var join = one.Joins[0];
        Assert.InRange(join.X, FirstRight(one), one.TargetX);
        Assert.True(one.TotalWidth >= one.TargetX + PlanTreeLayout.TargetWidth);

        // The svg must not carry a viewBox: the canvas stretches it to the window and a viewBox would scale the lines away from the nodes.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        var razor = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Paldium.App", "Pages", "Planner", "PlanTree.razor"));
        Assert.DoesNotContain("viewBox=", razor);
    }

    private static int FirstRight(PlanTreeLayout layout) => layout.Nodes.Min(n => n.X) + PlanTreeLayout.NodeWidth;
}
