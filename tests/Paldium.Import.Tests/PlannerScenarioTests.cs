using System.Text;
using Paldium.Core.Breeding;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;
using Paldium.Core.Probability;
using Paldium.Import.Saves;

namespace Paldium.Import.Tests;

public class PlannerScenarioTests
{
    private static string Describe(PlanNode node, int depth)
    {
        var pad = new string(' ', depth * 2);
        return node switch
        {
            BreedStep s => $"{pad}{s.SpeciesId} {s.Gender} (~{s.ExpectedEggs:0.0} eggs)\n{Describe(s.ParentA, depth + 1)}\n{Describe(s.ParentB, depth + 1)}",
            OwnedLeaf o => $"{pad}OWNED {o.Pal.SpeciesId} {o.Pal.Gender} Lv{o.Pal.Level} {o.Pal.Location.Kind} slot {o.Pal.Location.SlotIndex}",
            WildLeaf w => $"{pad}WILD {w.SpeciesId} {w.Gender} [{w.ObtainMethod}]",
            _ => pad + node.SpeciesId,
        };
    }

    [RealSaveFact(TestSupport.WorldVariable)]
    public async Task RealWorld_PrintsThePlannerScenarios()
    {
        var data = TestSupport.GameData;
        var breeding = BreedingCalculator.Load(data, Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"));
        var reader = new SaveWorldReader(data, Path.Combine(TestSupport.NewTempFolder(), "work"), null, (_, _) => { });
        var world = reader.ReadWorld(TestSupport.WorldFolder!);
        var owned = world.Pals;
        var finder = new SpeciesPathFinder(data, breeding);
        var sb = new StringBuilder($"Pals: {owned.Count}\n");

        async Task Run(string title, PathQuery query)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await finder.FindAsync(query, owned, CancellationToken.None);
            sb.AppendLine($"== {title} ({watch.ElapsedMilliseconds} ms): {result.Plans.Count} plan(s), diag: {string.Join(",", result.Diagnostics)}");
            foreach (var plan in result.Plans)
                sb.AppendLine($"-- steps {plan.Steps}, wild {plan.WildCount}, eggs ~{plan.ExpectedEggsTotal:0.0}, start {plan.UsesStartParent}, notes {string.Join(";", plan.Notes)}\n{Describe(plan.Root, 1)}");
        }

        var relaxaurus = owned.FirstOrDefault(p => p.SpeciesId == "LazyDragon");
        var ownedSpecies = owned.Select(p => p.SpeciesId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A target the user does not own yet, reachable from Relaxaurus.
        var target = data.Pals.First(p => !ownedSpecies.Contains(p.Id) && p.Rarity is >= 6 and <= 10 &&
                                          breeding.MinStepsToTarget("LazyDragon", p.Id) is > 0 and <= 3).Id;
        sb.AppendLine($"Target: {target}, owned Relaxaurus: {relaxaurus != null}");
        await Run("Sekhmet (already owned)", new PathQuery("Sekhmet", null, [], true, 2, true, 6, 3, OwnerScope.Everything));
        await Run($"{target}, start owned Relaxaurus",
            new PathQuery(target, relaxaurus == null ? null : new StartParent(relaxaurus.Uid, null, null), [], true, 2, true, 6, 3, OwnerScope.Everything));
        await Run($"{target}, no start", new PathQuery(target, null, [], true, 2, true, 6, 3, OwnerScope.Everything));
        await Run("Jetragon (legendary), MaxWild 4", new PathQuery("JetDragon", null, [], true, 4, true, 8, 3, OwnerScope.Everything));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "planner-scenarios.txt"), sb.ToString());
    }

    [RealSaveFact(TestSupport.WorldVariable)]
    public async Task RealWorld_PrintsThePassiveScenario()
    {
        var data = TestSupport.GameData;
        var breeding = BreedingCalculator.Load(data, Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"));
        var reader = new SaveWorldReader(data, Path.Combine(TestSupport.NewTempFolder(), "work"), null, (_, _) => { });
        var owned = reader.ReadWorld(TestSupport.WorldFolder!).Pals;
        var solver = new PassiveSolver(data, breeding, new PassiveOddsCalculator(data));
        var work = RolePresets.Build(data, RolePresets.WorkerNight, PresetVariant.Easy)!.PassiveIds;
        var sb = new StringBuilder($"Worker (easy) set: {string.Join(", ", work)}{Environment.NewLine}");

        // Pals that carry work speed passives.
        var carriers = owned.Where(p => p.Passives.Any(x => work.Contains(x, StringComparer.OrdinalIgnoreCase))).ToList();
        sb.AppendLine($"Pals with a work speed passive: {carriers.Count}");
        foreach (var species in new[] { "CowPal", "ChickenPal", "LazyDragon", "VioletFairy" })
            sb.AppendLine($"  {species}: {owned.Count(p => p.SpeciesId == species)} owned, {carriers.Count(p => p.SpeciesId == species)} with a work passive");

        async Task Run(string title, IReadOnlyList<OwnedPal> preferred, IReadOnlyList<string> desired, bool cake)
        {
            var goal = new GoalQuery("Sekhmet", desired, 0, GenderReq.Any, null, preferred.Select(p => p.Uid).ToList(), PreferredMode.None,
                true, 3, true, 1, 6, cake, 3, OwnerScope.Everything, TimeSpan.FromSeconds(30));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await solver.SolveAsync(goal, owned, CancellationToken.None);
            sb.AppendLine($"== {title} ({watch.ElapsedMilliseconds} ms): {result.Plans.Count} plan(s) {(result.TimedOut ? "TIMED OUT" : "")}, diag: {string.Join(",", result.Diagnostics)}");
            foreach (var f in result.Feasibility)
                sb.AppendLine($"   preferred {f.Uid}: {(f.BestPlan == null ? f.ReasonIfNone : $"{f.BestPlan.Steps} step(s), ~{f.BestPlan.ExpectedEggsTotal:0.0} eggs")}");
            foreach (var plan in result.Plans.Take(2))
                sb.AppendLine($"-- steps {plan.Steps}, wild {plan.WildCount}, eggs ~{plan.ExpectedEggsTotal:0.0}{Environment.NewLine}{Describe(plan.Root, 1)}");
        }

        var preferredPals = new[] { "CowPal", "ChickenPal", "LazyDragon", "VioletFairy" }
            .Select(s => carriers.Where(p => p.SpeciesId == s).OrderByDescending(p => p.Passives.Count(x => work.Contains(x))).FirstOrDefault())
            .OfType<OwnedPal>().ToList();
        var desired = preferredPals.SelectMany(p => p.Passives).Where(x => work.Contains(x, StringComparer.OrdinalIgnoreCase)).Distinct().Take(4).ToList();
        if (desired.Count == 0) desired = work.Take(2).ToList();
        sb.AppendLine($"Preferred: {string.Join(", ", preferredPals.Select(p => p.SpeciesId + " " + string.Join("+", p.Passives)))}; desired: {string.Join(", ", desired)}");
        if (preferredPals.Count > 0) await Run("Sekhmet, preferred work-speed Pals", preferredPals, desired, false);
        await Run("Sekhmet, work speed set, nothing preferred", [], work.Take(2).ToList(), false);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "planner-passive-scenarios.txt"), sb.ToString());
    }
}
