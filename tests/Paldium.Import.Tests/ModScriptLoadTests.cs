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

    [Fact]
    public void LuaHelpers_PreserveFalseBooleans_EscapeControlCharsWithoutBreakingZOrUtf8_HandleNaNInf_AndParseUtf8Titles()
    {
        var source = File.ReadAllText(MainLua);
        static string SliceBetween(string src, string startMarker, string endMarker)
        {
            var s = src.IndexOf(startMarker, StringComparison.Ordinal);
            var e = src.IndexOf(endMarker, s, StringComparison.Ordinal);
            Assert.True(s >= 0 && e > s, $"Slice not found: {startMarker} .. {endMarker}");
            return src[s..e];
        }

        using var lua = new Lua();
        lua.Encoding = System.Text.Encoding.UTF8;
        var memberSnippet = "function valid(o) return o ~= nil end\nfunction unwrap(v) return v end\n"
            + SliceBetween(source, "function member(", "function length(");
        lua.DoString(memberSnippet + "\nlocal obj = { Flag = false }\nlocal v, how = member(obj, 'Flag')\nreturn tostring(v) .. '|' .. tostring(how)");
        Assert.Equal("false|property", lua.ToString(-1));
        lua.Pop(lua.GetTop());

        var jsonSnippet = SliceBetween(source, "local function jsonString(", "function array(");
        lua.DoString(jsonSnippet + "\nreturn jsonValue({ z = 'Grizzbolt \u011f\u015f', nan = 0/0, inf = math.huge, ninf = -math.huge, ctrl = 'a\\nb', nilbyte = 'x\\0y' })");
        var json = lua.ToString(-1);
        lua.Pop(lua.GetTop());
        Assert.Contains("\"z\":\"Grizzbolt \u011f\u015f\"", json, StringComparison.Ordinal);
        Assert.Contains("\"nan\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"inf\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"ninf\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"ctrl\":\"a\\nb\"", json, StringComparison.Ordinal);
        Assert.Contains("\"nilbyte\":\"x\\u0000y\"", json, StringComparison.Ordinal);

        var titleSnippet = SliceBetween(source, "local function ownerNameFromTitle(", "uiMenus = {}");
        lua.DoString(titleSnippet + "\nreturn (ownerNameFromTitle('\u738b\u5c0f\u660e\u2019s Dimensional Pal Storage') or '') .. '|' .. (ownerNameFromTitle('Ali\\\'n\u0131n Boyutsal Pal Deposu') or '')");
        Assert.Equal("\u738b\u5c0f\u660e|Ali", lua.ToString(-1));
        lua.Pop(lua.GetTop());
    }

    [Fact]
    public void ReadRelics_DoesNotSetHaveToZero_WhenClientEntriesArrayIsEmpty()
    {
        var source = File.ReadAllText(MainLua);
        var start = source.IndexOf("local RELIC_TYPES = {", StringComparison.Ordinal);
        var end = source.IndexOf("-- T133: the cumulative effigy cost", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        using var lua = new Lua();
        lua.Encoding = System.Text.Encoding.UTF8;
        var setup = """
            function valid(o) return type(o) == 'table' end
            function unwrap(v) return v end
            function member(o, k) if valid(o) then return o[k] end return nil end
            function text(v) return v ~= nil and tostring(v) or nil end
            function number(v) return type(v) == 'number' and v or nil end
            function items(t) return type(t) == 'table' and t or nil end
            """;
        var script = setup + "\n" + source[start..end] + """
            local record = {
                RelicPossessNumArray = { Entries = {} },
                RelicObtainForInstanceFlag_CapturePower = { Items = { { Value = true }, { Value = false }, { Value = true } } },
            }
            local r, found = readRelics(record)
            return tostring(found) .. '|' .. tostring(r.CapturePower.have) .. '|' .. tostring(r.CapturePower.collected) .. '|' .. tostring(r.JumpPower)
            """;
        lua.DoString(script);
        Assert.Equal("true|nil|2|nil", lua.ToString(-1));
        lua.Pop(lua.GetTop());
    }

    [Fact]
    public void PickLocalController_PicksLocalOnHost_FallsBackToLocalPlayerOrSingleCandidate_AndNeverGuesses()
    {
        var source = File.ReadAllText(MainLua);
        var start = source.IndexOf("local function pickLocalController(", StringComparison.Ordinal);
        var end = source.IndexOf("local function controllerIsLocal(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "pickLocalController helper not found in main.lua");

        using var lua = new Lua();
        lua.Encoding = System.Text.Encoding.UTF8;
        var snippet = source[start..end];

        var script = snippet + """
            local function isLocal(c) return c.isLocal end
            local function pClass(c) return c.playerClass end

            local client = { id = 'client', isLocal = false, playerClass = 'NetConnection /Engine/Transient.NetConnection_0' }
            local host = { id = 'host', isLocal = true, playerClass = 'LocalPlayer /Engine/Transient.LocalPlayer_0' }
            local p1, m1 = pickLocalController({ client, host }, isLocal, pClass)

            local cNet = { id = 'remote', playerClass = 'NetConnection_1' }
            local cLoc = { id = 'localp', playerClass = 'PalLocalPlayer_0' }
            local p2, m2 = pickLocalController({ cNet, cLoc }, isLocal, pClass)

            local solo = { id = 'solo' }
            local p3, m3 = pickLocalController({ solo }, isLocal, pClass)

            local remoteOnly = { id = 'remoteSolo', isLocal = false, playerClass = 'NetConnection_2' }
            local p4, m4 = pickLocalController({ remoteOnly }, isLocal, pClass)

            local p5, m5 = pickLocalController({ { id = 'a' }, { id = 'b' } }, isLocal, pClass)

            return string.format('%s:%s|%s:%s|%s:%s|%s:%s|%s:%s',
                p1 and p1.id or 'nil', tostring(m1),
                p2 and p2.id or 'nil', tostring(m2),
                p3 and p3.id or 'nil', tostring(m3),
                p4 and p4.id or 'nil', tostring(m4),
                p5 and p5.id or 'nil', tostring(m5))
            """;
        lua.DoString(script);
        Assert.Equal("host:a|localp:b|solo:c|nil:nil|nil:nil", lua.ToString(-1));
        lua.Pop(lua.GetTop());
    }
}
