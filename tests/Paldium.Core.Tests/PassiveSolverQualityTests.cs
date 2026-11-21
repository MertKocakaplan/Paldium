using System.Diagnostics;
using System.Text;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;
using Paldium.Core.Probability;

namespace Paldium.Core.Tests;

/// <summary>Compares the default (narrowed) search with the exhaustive one on small random scenarios (T36).</summary>
[Collection("Heavy")]
public class PassiveSolverQualityTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<BreedingCalculator> Breeding = new(() => BreedingCalculator.Load(Data.Value, TestPaths.DataFolder));

    private sealed record Scenario(int Index, GoalQuery Goal, IReadOnlyList<OwnedPal> Owned);

    private static List<Scenario> Scenarios(int count)
    {
        var data = Data.Value;
        var rng = new Random(2026);
        var good = data.Passives.Where(p => p.IsStandard && !p.MutationExclusive && p.Rank > 0).Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
        var junk = data.Passives.Where(p => p.IsStandard && !p.MutationExclusive && p.Rank < 0).Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
        var result = new List<Scenario>();
        var attempts = 0;
        while (result.Count < count && attempts++ < 5000)
        {
            var steps = rng.Next(3, 6);
            var desired = good.OrderBy(_ => rng.Next()).Take(rng.Next(2, 5)).ToList();
            var owned = new List<OwnedPal>();
            for (var i = 0; i < rng.Next(15, 41); i++)
            {
                var passives = desired.OrderBy(_ => rng.Next()).Take(rng.Next(0, 5)).Concat(junk.OrderBy(_ => rng.Next()).Take(rng.Next(0, 3))).Take(4).ToList();
                owned.Add(PassiveSolverTests.Pal("s" + result.Count + "-" + i, data.Pals[rng.Next(data.Pals.Count)].Id,
                    rng.Next(2) == 0 ? Gender.Male : Gender.Female, passives));
            }

            var target = data.Pals[rng.Next(data.Pals.Count)].Id;
            if (owned.Any(p => p.SpeciesId == target)) continue;
            // Reachable: some owned species is close enough to the target.
            if (owned.Min(p => Breeding.Value.MinStepsToTarget(p.SpeciesId, target)) > steps) continue;
            var goal = new GoalQuery(target, desired, rng.Next(0, 3), GenderReq.Any, null, [], PreferredMode.None, rng.Next(2) == 0,
                rng.Next(1, 4), true, 1, steps, false, 3, OwnerScope.Everything, TimeSpan.FromSeconds(10));
            result.Add(new Scenario(result.Count, goal, owned));
        }

        return result;
    }

    // Default (two stages, 10 s) against the exhaustive reference (no narrowing, 60 s). Run with: dotnet test --filter Category=Quality
    [QualityFact]
    [Trait("Category", "Quality")]
    public void DefaultMode_MatchesTheExhaustiveSearch()
    {
        var scenarios = Scenarios(60);
        Assert.Equal(60, scenarios.Count);
        var rows = new System.Collections.Concurrent.ConcurrentBag<(int Index, string Target, double? Default, double? Reference, bool RefTimedOut, bool DefaultTimedOut, bool NoteOnTimeout, long DefaultMs, long RefMs)>();
        Parallel.ForEach(scenarios, new ParallelOptions { MaxDegreeOfParallelism = 3 }, s =>
        {
            var solver = PassiveSolverTests.Solver();
            var watch = Stopwatch.StartNew();
            var def = solver.Solve(s.Goal with { TimeLimit = TimeSpan.FromSeconds(10) }, s.Owned, CancellationToken.None);
            var defMs = watch.ElapsedMilliseconds;
            watch.Restart();
            // Reference: the improving search over all subsets without the fast stage's help, 60 seconds.
            var reference = PassiveSolverTests.Solver().Solve(s.Goal with { TimeLimit = TimeSpan.FromSeconds(60), Exhaustive = true }, s.Owned, CancellationToken.None);
            rows.Add((s.Index, s.Goal.TargetId, def.Plans.Count > 0 ? def.Plans[0].ExpectedEggsTotal : null,
                reference.Plans.Count > 0 ? reference.Plans[0].ExpectedEggsTotal : null, reference.TimedOut, def.TimedOut,
                !def.TimedOut || def.Diagnostics.Contains(PlanNotes.BetterMayExist), defMs, watch.ElapsedMilliseconds));
        });

        var sorted = rows.OrderBy(r => r.Index).ToList();
        var sb = new StringBuilder("index;target;default;reference;ratio;refTimedOut;defaultMs;referenceMs" + Environment.NewLine);
        foreach (var r in sorted)
            sb.AppendLine($"{r.Index};{r.Target};{r.Default?.ToString("0.00")};{r.Reference?.ToString("0.00")};" +
                          $"{(r.Default is { } d && r.Reference is { } e ? (d / e).ToString("0.000") : "-")};{r.RefTimedOut}/{r.DefaultTimedOut};{r.DefaultMs};{r.RefMs}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "solver-quality.csv"), sb.ToString());

        // Scenarios nobody can solve do not count (the generator only knows that a species is close enough, not that the passives can travel).
        var missed = sorted.Where(r => r.Default == null && r.Reference != null).ToList();
        Assert.True(missed.Count == 0, "Default found nothing where the reference did: " + string.Join(", ", missed.Select(r => r.Index)));

        // A run whose time ran out must say that a better plan may exist; the quality threshold only applies to runs that finished.
        Assert.All(sorted, r => Assert.True(r.NoteOnTimeout, $"scenario {r.Index}: timed out without the BetterMayExist note"));
        var comparable = sorted.Where(r => r.Default != null && r.Reference != null && !r.RefTimedOut && !r.DefaultTimedOut).ToList();
        Assert.NotEmpty(comparable);
        var same = comparable.Count(r => Math.Abs(r.Default!.Value - r.Reference!.Value) <= 0.01 * r.Reference.Value);
        Assert.True(same >= 0.9 * comparable.Count, $"{same} of {comparable.Count} comparable scenarios match");
        Assert.All(comparable, r => Assert.True(r.Default!.Value <= r.Reference!.Value * 1.25 + 1e-9, $"scenario {r.Index}: {r.Default} vs {r.Reference}"));
    }
}

/// <summary>Quality runs take minutes: skipped unless PALDIUM_QUALITY=1 (then: dotnet test --filter Category=Quality).</summary>
public sealed class QualityFactAttribute : FactAttribute
{
    public QualityFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PALDIUM_QUALITY") != "1")
            Skip = "Set PALDIUM_QUALITY=1 and run with --filter Category=Quality.";
    }
}
