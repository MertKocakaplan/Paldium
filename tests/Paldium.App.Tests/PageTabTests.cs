using Paldium.App.Components;

namespace Paldium.App.Tests;

public class PageTabTests
{
    [Theory]
    [InlineData("planner", "planner", true)]
    [InlineData("planner?target=SheepBall", "planner", true)]
    [InlineData("guides/02-passive-inheritance", "guides", true)]
    [InlineData("mypals?sources=open#top", "mypals", true)]
    [InlineData("Planner/", "planner", true)]
    [InlineData("paldex", "planner", false)]
    [InlineData("guidesX", "guides", false)]
    [InlineData("", "planner", false)]
    [InlineData("not-found-page", "settings", false)]
    public void ThePageTab_IsActive_OnAPathPrefixOfWholeSegments(string uri, string href, bool expected)
    {
        Assert.Equal(expected, PageTab.IsPathActive(uri, href));
    }
}
