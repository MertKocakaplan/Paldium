using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;
using Paldium.Core.Probability;

namespace Paldium.Core.Tests;

/// <summary>T119: the search runs on several threads but gives exactly what the one-thread search gave (the one before T119 too).</summary>
[Collection("Heavy")]
public class ParallelSolverTests
{
    private static PassiveSolver Solver(int threads) =>
        new(PassiveSolverTests.SharedData, PassiveSolverTests.SharedBreeding, new PassiveOddsCalculator(PassiveSolverTests.SharedData)) { MaxDegreeOfParallelism = threads };

    private static List<OwnedPal> Crowd(int count, IReadOnlyList<string> d, int seed)
    {
        var data = PassiveSolverTests.SharedData;
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(i => PassiveSolverTests.Pal("p" + i, data.Pals[rng.Next(data.Pals.Count)].Id,
            rng.Next(2) == 0 ? Gender.Male : Gender.Female, rng.Next(4) == 0 ? d.Take(rng.Next(1, 5)).ToList() : [])).ToList();
    }

    private static GoalQuery Stage1Only(GoalQuery q) => q with { TimeLimit = TimeSpan.Zero, Exhaustive = false };

    // Run with a tiny work threshold so even small searches are cut into chunks and run on several threads.
    private static T WithChunking<T>(Func<T> run)
    {
        var old = PassiveSolver.MinPairsPerWorker;
        PassiveSolver.MinPairsPerWorker = 1;
        try { return run(); }
        finally { PassiveSolver.MinPairsPerWorker = old; }
    }

    // Counters of the single-thread search (updated after QA 2.2.1 intermediate target gender cost fix): all thread counts must stay identical.
    [Theory]
    [InlineData("crowd6", 17873, 1239, "leaf:LotusDragon+ScorpionMan=10/1/0")]
    [InlineData("crowd4", 14231, 1239, "leaf:LotusDragon+ScorpionMan=10/1/0")]
    [InlineData("mozSekhmet", 2810, 1099, "ScorpionMan>=100/2/2")]
    [InlineData("mozVixy", 1202, 826, "WizardOwl>Carbunclo>=200/3/3")]
    public void StageOne_IsTheSameSearchAsBeforeTheParallelVersion(string scenario, long generated, long expanded, string firstPlan)
    {
        var (query, owned) = Scenario(scenario);
        foreach (var threads in new[] { 1, 4, 19 })
        {
            var solver = Solver(threads);
            var result = WithChunking(() => solver.Solve(Stage1Only(query), owned, CancellationToken.None));
            Assert.Equal(generated, solver.LastStats.Generated);
            Assert.Equal(expanded, solver.LastStats.Expanded);
            Assert.StartsWith(firstPlan, SolverBenchTests.Fingerprint(result));
        }
    }

    [Theory]
    [InlineData("crowd6")]
    [InlineData("mozSekhmet")]
    [InlineData("mozVixy")]
    public void StageOne_ResultDoesNotDependOnTheNumberOfThreads(string scenario)
    {
        var (query, owned) = Scenario(scenario);
        var one = SolverBenchTests.Fingerprint(Solver(1).Solve(Stage1Only(query), owned, CancellationToken.None));
        for (var run = 0; run < 3; run++)
        {
            var many = WithChunking(() => SolverBenchTests.Fingerprint(Solver(8).Solve(Stage1Only(query), owned, CancellationToken.None)));
            Assert.Equal(one, many);
        }
    }

    [Fact]
    public void ImprovingStage_ThatRunsToTheEnd_GivesTheSameResultOnOneAndManyThreads()
    {
        var (query, owned) = Scenario("mozVixy");
        var q = query with { TimeLimit = TimeSpan.FromMinutes(2), Exhaustive = true };
        var one = Solver(1).Solve(q, owned, CancellationToken.None);
        var many = WithChunking(() => Solver(8).Solve(q, owned, CancellationToken.None));
        Assert.False(one.TimedOut);
        Assert.False(many.TimedOut);
        Assert.Equal(SolverBenchTests.Fingerprint(one), SolverBenchTests.Fingerprint(many));
    }

    [Fact]
    public async Task Cancellation_StopsTheParallelSearchQuickly()
    {
        var d = PassiveSolverTests.WorkPassives();
        var (query, _) = Scenario("crowd6");
        // The fourth wanted passive is on nobody: the search has to look at everything before it gives up.
        var owned = Crowd(1000, d.Take(3).ToList(), 5);
        using var cts = new CancellationTokenSource();
        var task = Solver(8).SolveAsync(query with { TimeLimit = TimeSpan.FromMinutes(2) }, owned, cts.Token);
        await Task.Delay(300);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
    }

    private static (GoalQuery Query, List<OwnedPal> Owned) Scenario(string name)
    {
        var d = PassiveSolverTests.WorkPassives();
        List<OwnedPal> moz() => [PassiveSolverTests.Pal("m", "CowPal", Gender.Female, d)];
        return name switch
        {
            "crowd6" => (PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3, maxSteps: 6), Crowd(1000, d, 11)),
            "crowd4" => (PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3, maxSteps: 4), Crowd(1000, d, 11)),
            "mozSekhmet" => (PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3), moz()),
            "mozVixy" => (PassiveSolverTests.Goal("CuteFox", d, wild: true, maxWild: 3), moz()),
            _ => throw new ArgumentException(name),
        };
    }
}
