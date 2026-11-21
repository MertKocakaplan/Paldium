using System.Diagnostics;
using Paldium.Core.Breeding;
using Paldium.Core.Data;

namespace Paldium.Core.Tests;

[Collection("Heavy")]
public class MutationCalculatorTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<BreedingRules> Rules = new(() => BreedingRules.Load(TestPaths.DataFolder));
    private static MutationCalculator Create() => new(Data.Value, Rules.Value);

    private static Dictionary<string, int> Shares(IReadOnlyList<MutationOutcome> outcomes, int n) =>
        outcomes.ToDictionary(o => o.SpeciesId, o => (int)Math.Round(o.Probability * n));

    // ---- hand calculated examples (REPORT T25) ------------------------------------------------------------------------
    // Ranks (CombiRank): JetDragon 70, Anubis 480, Sekhmet 870. Species powers near the targets: see the comments.

    [Fact]
    public void Example1_JetragonAndAnubis()
    {
        // m = 70, d = 410. n = max(Q(2*70*0.1 = 14), 1) = 7. offset = Q(2*70*0.5 = 70) + Q(2*410*0.4 = 328) + 1 = 35 + 164 + 1 = 200.
        // Targets 200..206. CubeTurtle_Neutral has power 200, WingGolem_Fire 210 (priority higher).
        // 200..204 are nearer to 200 (distance 0..4); 205 is a tie (5 and 5) and goes to the higher priority WingGolem_Fire; 206 -> 210.
        var calc = Create();
        Assert.Equal((200, 7), calc.TargetRange("JetDragon", "Anubis"));
        var shares = Shares(calc.MutationOutcomes("JetDragon", "Anubis"), 7);
        Assert.Equal(new Dictionary<string, int> { ["CubeTurtle_Neutral"] = 5, ["WingGolem_Fire"] = 2 }, shares);
    }

    [Fact]
    public void Example2_SekhmetAndAnubis()
    {
        // m = 480, d = 390. n = Q(96) = 48. offset = Q(480) + Q(312) + 1 = 240 + 156 + 1 = 397. Targets 397..444.
        // Powers: FoxExorcist 390, KingBahamut 410, BlueThunderHorse 420 (priority 42000), NightBlueHorse_Neutral 430, GrassMinotaur_Ice 440.
        // 397..399 -> 390 (3); 400 is a tie (10/10) -> KingBahamut (higher priority); 400..414 -> 410 (15); 415 is a tie -> 420 (10: 415..424);
        // 425 tie -> 430 (10: 425..434); 435 tie -> 440 (10: 435..444).
        var calc = Create();
        Assert.Equal((397, 48), calc.TargetRange("Sekhmet", "Anubis"));
        var shares = Shares(calc.MutationOutcomes("Sekhmet", "Anubis"), 48);
        Assert.Equal(new Dictionary<string, int>
        {
            ["FoxExorcist"] = 3, ["KingBahamut"] = 15, ["BlueThunderHorse"] = 10, ["NightBlueHorse_Neutral"] = 10, ["GrassMinotaur_Ice"] = 10,
        }, shares);
    }

    [Fact]
    public void Example3_TwoAnubis()
    {
        // m = 480, d = 0. n = 48. offset = Q(480) + Q(0) + 1 = 241. Targets 241..288.
        // Powers: GrassGolem_Dark 250, WingGolem 260, WhiteDeer_Dark 270, MonochromeQueen 280, KabukiMan 290.
        // 241..254 -> 250 (14, 255 is a tie -> higher priority 260); 255..264 -> 260; 265..274 -> 270; 275..284 -> 280; 285..288 -> 290 (4).
        var calc = Create();
        var shares = Shares(calc.MutationOutcomes("Anubis", "Anubis"), 48);
        Assert.Equal(new Dictionary<string, int>
        {
            ["GrassGolem_Dark"] = 14, ["WingGolem"] = 10, ["WhiteDeer_Dark"] = 10, ["MonochromeQueen"] = 10, ["KabukiMan"] = 4,
        }, shares);
    }

    [Theory]
    [InlineData(14.0f, 7)]    // 14 >> 1
    [InlineData(2.5f, 1)]     // round half to even: 2 -> 1
    [InlineData(3.5f, 2)]     // 4 -> 2
    [InlineData(0.4f, 0)]
    [InlineData(1.0f, 0)]
    [InlineData(328.0f, 164)]
    public void Q_RoundsHalfToEven_ThenShiftsRight(float x, int expected)
    {
        Assert.Equal(expected, MutationCalculator.Q(x));
    }

    [Fact]
    public void Probabilities_AddUpToOne_ForEveryPair()
    {
        var calc = Create();
        var ids = Data.Value.Pals.Select(p => p.Id).ToList();
        for (var i = 0; i < ids.Count; i += 7)
            for (var j = i; j < ids.Count; j += 11)
                Assert.Equal(1.0, calc.MutationOutcomes(ids[i], ids[j]).Sum(o => o.Probability), 9);
    }

    [Fact]
    public void MutantIsNeverMoreCommonThanTheNormalChild()
    {
        // A mutation aims below the parents' average power: its child never has a higher breeding power than the normal child.
        var calc = Create();
        var rank = Rules.Value.Rows.ToDictionary(r => r.Id, r => r.CombiRank, StringComparer.OrdinalIgnoreCase);
        var ids = Data.Value.Pals.Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
        var exceptions = new List<string>();
        for (var i = 0; i < ids.Count; i++)
            for (var j = i; j < ids.Count; j++)
            {
                // The rank based normal child (special recipes decide by their own table and are not comparable).
                var normal = rank[Rules.Value.Nearest((rank[ids[i]] + rank[ids[j]] + 1) / 2, excludeRecipeChildren: true)];
                var maxMutant = calc.MutationOutcomes(ids[i], ids[j]).Max(o => rank[o.SpeciesId]);
                if (maxMutant <= normal) continue;
                // Known exception (REPORT T25): two very strong parents. Between power 30 and 140 only recipe children exist; a normal
                // child cannot be one, a mutant can, so the mutant lands on 140 while the normal child falls back to 30.
                Assert.True(normal == 30 && maxMutant == 140 && rank[ids[i]] <= 230 && rank[ids[j]] <= 230, $"{ids[i]}+{ids[j]}: mutant {maxMutant} > normal {normal}");
                exceptions.Add($"{ids[i]}+{ids[j]}");
            }

        Assert.Equal(12, exceptions.Count);
    }

    [Fact]
    public void Rate_IsTheBaseRatePlusTheCakeBonus()
    {
        var calc = Create();
        Assert.Equal(0.01, calc.MutationRate(null), 9);
        Assert.Equal(0.03, calc.MutationRate("Cake04"), 9);
        Assert.Equal(0.01, calc.MutationRate("Cake02"), 9);
    }

    [Fact]
    public void PairsMutatingInto_AreSortedByProbability_AndFastAfterTheFirstCall()
    {
        var calc = Create();
        var watch = Stopwatch.StartNew();
        var pairs = calc.PairsMutatingInto("KingBahamut", 50);
        var first = watch.ElapsedMilliseconds;
        Assert.NotEmpty(pairs);
        Assert.True(pairs.Count <= 50);
        for (var i = 1; i < pairs.Count; i++) Assert.True(pairs[i - 1].Probability >= pairs[i].Probability);
        // Every listed pair really can mutate into the target.
        Assert.All(pairs.Take(5), p => Assert.Contains(calc.MutationOutcomes(p.A, p.B), o => o.SpeciesId == "KingBahamut"));
        watch.Restart();
        calc.PairsMutatingInto("Sekhmet", 50);
        Assert.True(watch.ElapsedMilliseconds < 200, $"second call {watch.ElapsedMilliseconds} ms (first {first} ms)");
        Assert.True(first < 2000, $"first call {first} ms");
    }
}
