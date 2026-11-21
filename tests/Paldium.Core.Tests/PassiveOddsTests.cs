using Paldium.Core.Data;
using Paldium.Core.Probability;

namespace Paldium.Core.Tests;

// CPU heavy or timing sensitive tests run one after another (the bundle load test has a one second limit).
[Collection("Heavy")]
public class PassiveOddsTests
{
    private static readonly Lazy<PassiveOddsCalculator> Calc = new(() => new PassiveOddsCalculator(GameDataLoader.Load(TestPaths.DataFolder)));

    [Theory]
    [InlineData(4, 0.10)]
    [InlineData(3, 0.12)]
    [InlineData(2, 0.24)]
    [InlineData(1, 0.40)]
    public void PoolEqualsTarget_KnownValues(int k, double expected)
    {
        Assert.Equal(expected, Calc.Value.Probability(k, 0, k, 0), 9);
    }

    [Fact]
    public void Outcomes_AddUpToOne_AndMatchTheMockupStep()
    {
        // The mockup's first step: a pool of four wanted passives (Usta, Ciddi, Kole Isci, Sansli), the step needs three of them (H = 3),
        // no unwanted one allowed, a male is wanted (one half): 1 %, 5 %, 5 %, 1 %, 3 %, 85 % (planner.md "every egg").
        var o = Calc.Value.Outcomes(k: 4, u: 0, h: 3, w: 0, genderRight: 0.5);
        Assert.Equal(1.0, o.Total, 9);
        Assert.Equal([1, 5, 5, 1, 3, 85], new[] { o.Wanted, o.ExtraWanted, o.OtherGenderExtra, o.OtherGender, o.ExtraUnwanted, o.Missing }.Select(x => (int)Math.Round(x * 100)));
        // The plan counts classes 1 and 2, which is exactly the success chance of the same step.
        Assert.Equal(Calc.Value.Probability(4, 0, 3, 0) * 0.5, o.PlanSuccess, 9);
    }

    [Fact]
    public void Outcomes_WithoutAGenderWish_HaveNoOtherGenderClasses()
    {
        var o = Calc.Value.Outcomes(k: 4, u: 0, h: 4, w: 0, genderRight: 1.0);
        Assert.Equal(0, o.OtherGender + o.OtherGenderExtra, 12);
        Assert.Equal(1.0, o.Total, 9);
        Assert.Equal(0.10, o.PlanSuccess, 9);
    }

    [Fact]
    public void SixPassivePool_FourWanted_IsOneFifteenthOfTen()
    {
        Assert.Equal(0.10 / 15, Calc.Value.Probability(4, 2, 4, 0), 9);
    }

    [Fact]
    public void SpecialCake_Cases()
    {
        Assert.Equal(1.0, Calc.Value.Probability(4, 0, 4, 0, specialCount: 4), 9);
        Assert.Equal(1.0, Calc.Value.Probability(2, 0, 2, 2, specialCount: 4), 9);
        Assert.Equal(0.0, Calc.Value.Probability(2, 0, 2, 1, specialCount: 4), 9);
    }

    [Fact]
    public void Compute_BuildsPoolAndReportsWarnings()
    {
        var q = new PassiveOddsQuery(["A", "B", "C", "D"], ["X"], ["A", "B", "C", "D"], 0);
        var r = Calc.Value.Compute(q);
        Assert.Equal(Calc.Value.Probability(4, 1, 4, 0), r.ProbabilityPerEgg, 12);
        Assert.Equal(1 / r.ProbabilityPerEgg, r.ExpectedEggs, 9);
        Assert.Contains(PassiveOddsWarnings.RandomAddFailure, r.Warnings);
        Assert.Contains(PassiveOddsWarnings.PoolTooLarge, r.Warnings);
        Assert.Equal(1.0, r.InheritCountDistribution.Sum(d => d.p), 9);

        var missing = Calc.Value.Compute(new PassiveOddsQuery(["A"], [], ["A", "Z"], 0));
        Assert.Equal(0, missing.ProbabilityPerEgg);
        Assert.True(double.IsPositiveInfinity(missing.ExpectedEggs));
        Assert.Contains(PassiveOddsWarnings.DesiredMissing, missing.Warnings);

        var cake = Calc.Value.Compute(new PassiveOddsQuery(["A", "B", "C", "D"], [], ["A", "B", "C", "D"], 0, "Cake05"));
        Assert.Equal(1.0, cake.ProbabilityPerEgg, 9);
    }

    // Step by step simulation of the game: pick the number to inherit, take them from the pool, add random passives.
    private static double Simulate(Random rng, int k, int u, int h, int w, bool special, int trials)
    {
        var data = GameDataLoader.Load(TestPaths.DataFolder).Mechanics;
        double[] Cum(IReadOnlyDictionary<int, int> weights, int min, int max)
        {
            var total = Enumerable.Range(min, max - min + 1).Sum(i => weights[i]);
            var acc = 0.0;
            return Enumerable.Range(min, max - min + 1).Select(i => acc += weights[i] / (double)total).ToArray();
        }

        var inherit = Cum(data.PassiveInheritWeights, 1, 4);
        var random = Cum(data.PassiveRandomAddWeights, 0, 3);
        int Draw(double[] cum, int offset) { var x = rng.NextDouble(); for (var i = 0; i < cum.Length; i++) if (x < cum[i]) return i + offset; return cum.Length - 1 + offset; }

        var n = k + u;
        var ok = 0;
        var pool = new int[n]; // 0..k-1 wanted, k.. unwanted
        for (var t = 0; t < trials; t++)
        {
            for (var i = 0; i < n; i++) pool[i] = i;
            var x = special ? 4 : Draw(inherit, 1);
            var xe = Math.Min(x, n);
            for (var i = 0; i < xe; i++) { var j = rng.Next(i, n); (pool[i], pool[j]) = (pool[j], pool[i]); }
            var wanted = 0; var unwanted = 0;
            for (var i = 0; i < xe; i++) { if (pool[i] < k && pool[i] < h) wanted++; else if (pool[i] >= k) unwanted++; }
            // Wanted passives beyond the h targets are harmless, they are neither counted as target nor unwanted.
            var slots = 4 - xe;
            var added = special ? slots : Math.Min(Draw(random, 0), slots);
            unwanted += added;
            if (wanted == h && unwanted <= w) ok++;
        }

        return ok / (double)trials;
    }

    [Fact]
    public void MonteCarlo_Matches_Formula_In30RandomScenarios()
    {
        var rng = new Random(12345);
        for (var s = 0; s < 30; s++)
        {
            var k = rng.Next(0, 5);
            var u = rng.Next(0, 5 - k + 1);
            var h = rng.Next(0, k + 1);
            var w = rng.Next(0, 4);
            var special = rng.Next(3) == 0;
            var formula = Calc.Value.Probability(k, u, h, w, special ? 4 : 0);
            var simulated = Simulate(rng, k, u, h, w, special, 200_000);
            Assert.True(Math.Abs(formula - simulated) <= 0.005, $"k={k} u={u} h={h} w={w} special={special}: {formula} vs {simulated}");
        }
    }
}
