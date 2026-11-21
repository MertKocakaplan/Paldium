using System.Text.RegularExpressions;
using KeraLua;

namespace Paldium.Import.Tests;

/// <summary>
/// Guards the Lua script of the assist mod (T56). UE4SS 3.0.1 runs Lua 5.4, which allows at most 200 active local
/// variables in a function, the main chunk included. The script once crossed that limit and the game refused to load it.
/// </summary>
public class ModScriptLoadTests
{
    /// <summary>The main chunk keeps room for future locals below the hard limit of 200.</summary>
    public const int TopLevelLocalBudget = 150;

    private static string MainLua => Path.Combine(TestSupport.RepoRoot, "mod", "PaldiumBridge", "Scripts", "main.lua");

    [Fact]
    public void MainScript_CompilesWithLua54()
    {
        var source = File.ReadAllBytes(MainLua);
        using var lua = new Lua();
        lua.DoString("return _VERSION");
        Assert.StartsWith("Lua 5.4", lua.ToString(-1));
        lua.Pop(1);

        var status = lua.LoadBuffer(source, "@main.lua");
        var message = status == LuaStatus.OK ? "" : lua.ToString(-1);
        Assert.True(status == LuaStatus.OK, "main.lua does not compile: " + message);
    }

    [Fact]
    public void MainScript_KeepsTheTopLevelLocalsUnderTheBudget()
    {
        var count = CountTopLevelLocals(File.ReadAllText(MainLua));
        Assert.True(count <= TopLevelLocalBudget,
            $"main.lua declares {count} local variables in the main chunk (budget {TopLevelLocalBudget}, Lua 5.4 limit 200). " +
            "Put sections in 'do -- section' ... 'end -- section' blocks and pre-declare only what other sections use.");
    }

    [Fact]
    public void TheEffigyRankTable_OfTheScript_MatchesTheDataBundle_AndGivesTheLevelOfASpentCount()
    {
        var source = File.ReadAllText(MainLua);
        var start = source.IndexOf("local RELIC_THRESHOLDS = {", StringComparison.Ordinal);
        var end = source.IndexOf("local captureEmpty", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the rank table section was not found in main.lua");

        using var lua = new Lua();
        var snippet = source[start..end];   // locals only; every check runs the snippet again and returns a value

        foreach (var relic in TestSupport.GameData.Relics)
        {
            var cumulative = 0;
            var expected = new List<int>();
            foreach (var rank in relic.Ranks) { cumulative += rank.Required; expected.Add(cumulative); }

            lua.DoString(snippet + $"\nlocal t = RELIC_THRESHOLDS[\"{relic.Type}\"] or {{}}\nlocal out = {{}}\nfor i, v in ipairs(t) do out[#out + 1] = tostring(v) end\nreturn table.concat(out, \",\")");
            Assert.Equal(string.Join(",", expected), lua.ToString(-1));
            lua.Pop(lua.GetTop());

            // The level of a spent count is the highest rank whose cumulative cost fits (checked on the last threshold and one below it).
            foreach (var spent in new[] { 0, expected[0] - 1, expected[0], expected[^1] - 1, expected[^1], expected[^1] + 5 })
            {
                var level = expected.Count(c => c <= spent);
                lua.DoString(snippet + $"\nreturn relicLevel(\"{relic.Type}\", {spent})");
                Assert.Equal(level, (int)lua.ToInteger(-1));
                lua.Pop(lua.GetTop());
            }
        }
    }

    [Fact]
    public void TheCounter_CountsNamesAndSkipsBlocksAndIndentedLines()
    {
        const string sample = "local a, b = 1, 2\nlocal function f() local inner = 1 end\n  local indented = 1\n" +
                              "local x\ndo -- section\nlocal hidden, hidden2 = 1, 2\nend -- section\nlocal y = 3 -- c, d\n";
        Assert.Equal(5, CountTopLevelLocals(sample)); // a, b, f, x, y
    }

    /// <summary>Counts the names declared by <c>local</c> statements that start at column 0 outside the section blocks.</summary>
    public static int CountTopLevelLocals(string source)
    {
        var count = 0;
        var inBlock = false;
        foreach (var line in source.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith("do -- section", StringComparison.Ordinal)) { inBlock = true; continue; }
            if (text.StartsWith("end -- section", StringComparison.Ordinal)) { inBlock = false; continue; }
            if (inBlock || !text.StartsWith("local ", StringComparison.Ordinal)) continue;
            if (text.StartsWith("local function ", StringComparison.Ordinal)) { count++; continue; }
            var names = Regex.Replace(text[6..], @"--.*$", "").Split('=')[0];
            count += names.Split(',').Count(n => n.Trim().Length > 0);
        }
        return count;
    }
}
