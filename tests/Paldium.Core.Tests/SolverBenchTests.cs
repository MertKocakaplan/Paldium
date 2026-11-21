using System.Diagnostics;
using System.Globalization;
using System.Text;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;
using Paldium.Core.Probability;

namespace Paldium.Core.Tests;

/// <summary>
/// Measurements for T119 (not a pass/fail test): with the environment variable PALBENCH_OUT set to a file path it runs the scenarios
/// and appends the times, the search counters, a fingerprint of the result and the memory to that file. Without it, it does nothing.
/// </summary>
[Collection("Heavy")]
public class SolverBenchTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static PassiveSolver Solver(int threads) =>
        new(Data.Value, PassiveSolverTests.SharedBreeding, new PassiveOddsCalculator(Data.Value)) { MaxDegreeOfParallelism = threads };

    private static List<OwnedPal> Crowd(int count, IReadOnlyList<string> d, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(i => PassiveSolverTests.Pal("p" + i, Data.Value.Pals[rng.Next(Data.Value.Pals.Count)].Id,
            rng.Next(2) == 0 ? Gender.Male : Gender.Female, rng.Next(4) == 0 ? d.Take(rng.Next(1, 5)).ToList() : [])).ToList();
    }

    /// <summary>A short text that changes when any plan, its cost or its path changes.</summary>
    internal static string Fingerprint(PlanResult r) =>
        string.Join(" | ", r.Plans.Select(p => $"{SpeciesPathFinder.PathKeyOf(p.Root)}={p.ExpectedEggsTotal.ToString("R", CultureInfo.InvariantCulture)}/{p.Steps}/{p.WildCount}"));

    [Fact]
    [Trait("Category", "Bench")]
    public void Measure()
    {
        var path = Environment.GetEnvironmentVariable("PALBENCH_OUT");
        if (string.IsNullOrEmpty(path)) return;
        var threadCounts = (Environment.GetEnvironmentVariable("PALBENCH_THREADS") ?? "1,0").Split(',').Select(int.Parse).ToArray(); // 0 = default
        if (int.TryParse(Environment.GetEnvironmentVariable("PALBENCH_PPW"), out var ppw)) PassiveSolver.MinPairsPerWorker = ppw;
        var d = PassiveSolverTests.WorkPassives();
        var log = new StringBuilder();
        log.AppendLine($"=== {DateTime.Now:s} logical processors {Environment.ProcessorCount}, pairs per worker {PassiveSolver.MinPairsPerWorker} ===");

        var scenarios = new List<(string Name, Func<GoalQuery> Goal, Func<List<OwnedPal>> Owned)>
        {
            ("1000 Pals, |D|=4, MaxSteps 6", () => PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3, maxSteps: 6), () => Crowd(1000, d, 11)),
            ("1000 Pals, |D|=4, MaxSteps 4", () => PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3, maxSteps: 4), () => Crowd(1000, d, 11)),
            ("Mozzarina -> Sekhmet, 4 passives", () => PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3), () => [PassiveSolverTests.Pal("m", "CowPal", Gender.Female, d)]),
            ("Mozzarina -> Vixy, 4 passives", () => PassiveSolverTests.Goal("CuteFox", d, wild: true, maxWild: 3), () => [PassiveSolverTests.Pal("m", "CowPal", Gender.Female, d)]),
        };

        foreach (var (name, goal, owned) in scenarios)
        {
            foreach (var requested in threadCounts)
            {
                var threads = requested <= 0 ? Math.Max(1, Environment.ProcessorCount - 1) : requested;
                var solver = Solver(threads);
                var pals = owned();
                var query = goal() with { TimeLimit = TimeSpan.Zero, Exhaustive = false };

                var reps = int.TryParse(Environment.GetEnvironmentVariable("PALBENCH_REPS"), out var r) ? r : 3;
                long ms1 = long.MaxValue; TimeSpan cpu1 = default; PlanResult stage1 = null!; SolverStats stats1 = default!; long alloc1 = 0;
                for (var rep = 0; rep < reps; rep++)
                {
                    GC.Collect();
                    var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
                    var alloc0 = GC.GetTotalAllocatedBytes(true);
                    var watch0 = Stopwatch.StartNew();
                    var run = solver.Solve(query, pals, CancellationToken.None);
                    var elapsed = watch0.ElapsedMilliseconds;
                    if (elapsed < ms1) { ms1 = elapsed; cpu1 = Process.GetCurrentProcess().TotalProcessorTime - cpu0; stage1 = run; stats1 = solver.LastStats; alloc1 = GC.GetTotalAllocatedBytes(true) - alloc0; }
                }

                var watch = Stopwatch.StartNew();
                var peak = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576;
                log.AppendLine($"[{name}] threads {threads}: stage 1 best of {reps}: {ms1} ms, cpu {cpu1.TotalMilliseconds:F0} ms ({cpu1.TotalMilliseconds / Math.Max(1, ms1):F1} cores), " +
                               $"generated {stats1.Generated}, expanded {stats1.Expanded}, pairs {stats1.Pairs}, allocated {alloc1 / 1048576} MB, peak working set so far {peak} MB");
                log.AppendLine($"    result: {(stage1.Plans.Count == 0 ? "no plan" : stage1.Plans[0].ExpectedEggsTotal.ToString("F3", CultureInfo.InvariantCulture) + " eggs")}; {Fingerprint(stage1)}");

                // Stage 2 inside a fixed time (the best plan it reaches), after stage 1.
                var improveQuery = query with { TimeLimit = TimeSpan.FromSeconds(10) };
                var cpuB = Process.GetCurrentProcess().TotalProcessorTime;
                watch.Restart();
                var full = solver.Solve(improveQuery, pals, CancellationToken.None);
                var ms = watch.ElapsedMilliseconds;
                var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuB;
                log.AppendLine($"    stage 1 + stage 2 (10 s limit): {ms} ms, cpu {cpu.TotalMilliseconds:F0} ms ({cpu.TotalMilliseconds / Math.Max(1, ms):F1} cores), " +
                               $"best {(full.Plans.Count == 0 ? "none" : full.Plans[0].ExpectedEggsTotal.ToString("F3", CultureInfo.InvariantCulture))} eggs, timed out {full.TimedOut}, " +
                               $"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
            }
        }

        File.AppendAllText(path, log.ToString());
    }
}
