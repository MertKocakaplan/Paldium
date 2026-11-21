using System.Diagnostics;
using System.Text.Json;
using Paldium.Core.Data.Schema;

namespace Paldium.DataBuilder;

/// <summary>Reads PalCalc's db.json / breeding.json and maps them to the Paldium bundle schema.</summary>
public sealed class PalCalcSource
{
    private static readonly string[] WorkTypeOrder =
    [
        "Kindling", "Watering", "Planting", "GenerateElectricity", "Handiwork", "Gathering",
        "Lumbering", "Mining", "MedicineProduction", "Cooling", "Transporting", "Farming",
    ];

    // PalCalc icon file name differs from the enum name for one work type.
    private static readonly Dictionary<string, string> WorkIconFile = new() { ["GenerateElectricity"] = "ElectricityGeneration" };

    private static readonly Dictionary<string, string> WorkEnglishNames = new()
    {
        ["Kindling"] = "Kindling",
        ["Watering"] = "Watering",
        ["Planting"] = "Planting",
        ["GenerateElectricity"] = "Generating Electricity",
        ["Handiwork"] = "Handiwork",
        ["Gathering"] = "Gathering",
        ["Lumbering"] = "Lumbering",
        ["Mining"] = "Mining",
        ["MedicineProduction"] = "Medicine Production",
        ["Cooling"] = "Cooling",
        ["Transporting"] = "Transporting",
        ["Farming"] = "Farming",
    };

    private readonly string _palcalcDir;
    private readonly JsonElement _db;
    private readonly JsonElement _breedingRows;

    public List<string> Warnings { get; } = [];
    public List<string> MissingIcons { get; } = [];
    public List<string> UnmappedPassives { get; } = [];

    public PalCalcSource(string palcalcDir)
    {
        _palcalcDir = palcalcDir;
        var model = Path.Combine(palcalcDir, "PalCalc.Model");
        _db = JsonDocument.Parse(File.ReadAllText(Path.Combine(model, "db.json"))).RootElement;
        _breedingRows = JsonDocument.Parse(File.ReadAllText(Path.Combine(model, "breeding.json")))
            .RootElement.GetProperty("Breeding");
    }

    public string DbVersion => _db.GetProperty("Version").GetString()!;

    public static string GitCommit(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"-C \"{dir}\" rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static SortedDictionary<string, string> Localized(JsonElement obj)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (obj.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in obj.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString()!;
        return d;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int def = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    /// <summary>The breeding table exactly as PalCalc ships it (cross-check of the rule engine, T22).</summary>
    public BreedingFile BuildPalCalcBreeding() => BuildBreeding(out _, out _);

    public (GameDataFile GameData, BreedingFile Breeding) Build(string generatedAtUtc, string commit,
        string passiveTagsPath, string outDir, ExtractorSource? extractor = null)
    {
        // The game rules (T22) are the source of the breeding table; the PalCalc table is the fallback and the cross-check.
        var rules = extractor?.BreedingRules();
        var palCalcBreeding = BuildBreeding(out var pairCounts, out var sameSpeciesOnly);
        var breeding = palCalcBreeding;
        if (rules != null)
        {
            var fromRules = RulesBreeding.Build(rules, palCalcBreeding.PalOrder);
            breeding = fromRules.File;
            pairCounts = fromRules.ParentPairCounts;
            sameSpeciesOnly = fromRules.SameSpeciesOnly;
        }
        else
        {
            Warnings.Add("No extractor data: breeding.json comes from the PalCalc table, not from the game rules.");
        }

        var tagger = new PassiveTagger(passiveTagsPath);
        var palIndexById = breeding.PalOrder.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);

        var palsJson = _db.GetProperty("Pals").EnumerateArray().ToList();
        // Game table order and special recipe children (T22).
        var gameOrder = rules?.Rows.ToDictionary(r => r.Id, r => r.Order, StringComparer.OrdinalIgnoreCase);
        var recipeChildren = rules?.Recipes.Select(r => r.Child).ToHashSet(StringComparer.OrdinalIgnoreCase);
        extractor?.PrepareObtain(palsJson.Select(p => p.GetProperty("InternalName").GetString()!));
        var ranchByPal = extractor?.RanchByPal();
        var genderProb = _db.GetProperty("BreedingGenderProbability");
        var mech = _db.GetProperty("BreedingMechanics");

        var pals = new List<PalDto>();
        var iconOverrides = LoadIconOverrides();
        var palIconDir = Path.Combine(_palcalcDir, "PalCalc.UI", "Resources", "Pals");
        foreach (var p in palsJson
                     .OrderBy(p => p.GetProperty("Id").GetProperty("PalDexNo").GetInt32())
                     .ThenBy(p => p.GetProperty("Id").GetProperty("IsVariant").GetBoolean())
                     .ThenBy(p => p.GetProperty("InternalName").GetString(), StringComparer.Ordinal))
        {
            var id = p.GetProperty("InternalName").GetString()!;
            var name = p.GetProperty("Name").GetString()!;
            var dexNo = p.GetProperty("Id").GetProperty("PalDexNo").GetInt32();
            var isVariant = p.GetProperty("Id").GetProperty("IsVariant").GetBoolean();
            var rarity = Int(p, "Rarity");
            var hp = Int(p, "Hp");
            var attack = Int(p, "Attack");
            var defense = Int(p, "Defense");
            var male = Math.Round(genderProb.GetProperty(id).GetProperty("MALE").GetDouble(), 6);

            var iconFile = iconOverrides.TryGetValue(name, out var o) ? o : name + ".png";
            var iconSrc = Path.Combine(palIconDir, iconFile);
            if (File.Exists(iconSrc))
                BundleWriter.CopyIcon(iconSrc, Path.Combine(outDir, "icons", "pals", id + ".png"));
            else
                MissingIcons.Add($"{id} ({iconFile})");

            var work = new SortedDictionary<string, int>(StringComparer.Ordinal);
            if (p.TryGetProperty("WorkSuitability", out var ws) && ws.ValueKind == JsonValueKind.Object)
                foreach (var w in ws.EnumerateObject())
                    if (w.Value.GetInt32() != 0) work[w.Name] = w.Value.GetInt32();

            LevelRangeDto? wild = null;
            if (p.TryGetProperty("MinWildLevel", out var mn) && mn.ValueKind == JsonValueKind.Number &&
                p.TryGetProperty("MaxWildLevel", out var mx) && mx.ValueKind == JsonValueKind.Number)
                wild = new LevelRangeDto(mn.GetInt32(), mx.GetInt32());

            var learnset = p.TryGetProperty("InternalAttackLeveling", out var al)
                ? al.EnumerateArray().Select(a => new LearnsetEntryDto(
                    a.GetProperty("AttackInternalId").GetString()!, a.GetProperty("Level").GetInt32())).ToList()
                : [];
            var guaranteed = p.TryGetProperty("GuaranteedPassivesInternalIds", out var gp)
                ? gp.EnumerateArray().Select(x => x.GetString()!).ToList()
                : [];

            IReadOnlyList<string> palElements = [];
            PartnerSkillDto? partnerSkill = null;
            IReadOnlyList<DropDto>? drops = null;
            var uncapturable = false;
            MountDto? mount = null;
            IReadOnlyList<RanchRankDto>? ranch = null;
            if (ranchByPal != null && ranchByPal.TryGetValue(id, out var ranchRanks)) ranch = ranchRanks;
            if (extractor != null)
            {
                if (extractor.TryGetSpecies(id, out _))
                {
                    var g = extractor.Overrides(id, rarity, hp, attack, defense, male, work);
                    // The game table wins over PalCalc where they disagree (counted by the extractor source).
                    (rarity, hp, attack, defense, male, work) = (g.Rarity, g.Hp, g.Attack, g.Defense, g.MaleProbability, g.Work);
                    palElements = g.Elements;
                    partnerSkill = g.PartnerSkill;
                    drops = g.Drops;
                    uncapturable = g.IsUncapturable;
                    mount = g.Mount;
                }
                else Warnings.Add($"No game row for species {id}; PalCalc values kept.");
            }

            var palIndex = palIndexById[id];
            pals.Add(new PalDto(
                Id: id,
                DexNo: dexNo,
                IsVariant: isVariant,
                DexLabel: dexNo + (isVariant ? "B" : ""),
                TableOrder: gameOrder != null && gameOrder.TryGetValue(id, out var order) ? order : Int(p, "InternalIndex"),
                Names: Localized(p.GetProperty("LocalizedNames")),
                Elements: palElements,
                Rarity: rarity,
                Size: Str(p, "Size") ?? "None",
                Nocturnal: p.TryGetProperty("Nocturnal", out var noc) && noc.GetBoolean(),
                Price: Int(p, "Price"),
                BreedingPower: Int(p, "BreedingPower"),
                BreedingPriority: Int(p, "BreedingPowerPriority"),
                MaleProbability: male,
                Stats: new StatsDto(hp, attack, defense, Int(p, "WalkSpeed"),
                    Int(p, "RunSpeed"), Int(p, "RideSprintSpeed"), Int(p, "TransportSpeed"), Int(p, "Stamina"),
                    Int(p, "MaxFullStomach"), Int(p, "FoodAmount"), Int(p, "CraftSpeed")),
                Work: work,
                WildLevel: wild,
                AttackLearnset: learnset,
                GuaranteedPassives: guaranteed,
                Egg: new EggDto(palElements.Count > 0 ? palElements[0] : null, EggSize(rarity)),
                Icon: $"icons/pals/{id}.png",
                BreedingFlags: new BreedingFlagsDto(sameSpeciesOnly[palIndex], pairCounts[palIndex], recipeChildren?.Contains(id), pairCounts[palIndex] == 0),
                Obtain: extractor?.ObtainData?.Build(id, sameSpeciesOnly[palIndex], recipeChildren?.Contains(id) == true),
                PartnerSkill: partnerSkill,
                Drops: drops,
                IsUncapturable: uncapturable,
                // Only Pals with Ranch work (Farming) can be assigned to the Ranch (DT_MapObjectAssignData MonsterFarm needs rank 1); the spawn item data of the
                // others (Teafant, Direhowl, Tarantriss, Snock) is not used there (T116).
                Ranch: work.ContainsKey("Farming") ? ranch : null,
                Group: id.StartsWith("Yakushima", StringComparison.Ordinal) ? "terraria" : null,
                Mount: mount));
        }

        // Passives: standard ones plus anything a Pal guarantees.
        var guaranteedIds = pals.SelectMany(p => p.GuaranteedPassives).ToHashSet(StringComparer.Ordinal);
        var passives = new List<PassiveDto>();
        foreach (var p in _db.GetProperty("PassiveSkills").EnumerateArray()
                     .OrderBy(p => p.GetProperty("InternalName").GetString(), StringComparer.Ordinal))
        {
            var id = p.GetProperty("InternalName").GetString()!;
            var standard = p.TryGetProperty("IsStandardPassiveSkill", out var s) && s.GetBoolean();
            if (!standard && !guaranteedIds.Contains(id)) continue;

            var rank = Int(p, "Rank");
            var (tags, penaltyTags, mapped) = tagger.Tag(id, rank);
            if (!mapped) UnmappedPassives.Add(id);
            passives.Add(new PassiveDto(
                Id: id,
                Names: Localized(p.GetProperty("LocalizedNames")),
                Descriptions: p.TryGetProperty("LocalizedDescriptions", out var ld)
                    ? Localized(ld)
                    : new SortedDictionary<string, string>(StringComparer.Ordinal),
                Rank: rank,
                IsStandard: standard,
                CanAppearRandomly: p.TryGetProperty("RandomInheritanceAllowed", out var ra) && ra.GetBoolean(),
                RandomWeight: Int(p, "RandomInheritanceWeight"),
                Surgery: new SurgeryDto(
                    p.TryGetProperty("SupportsSurgery", out var ss) && ss.GetBoolean(),
                    Int(p, "SurgeryCost"),
                    Str(p, "SurgeryRequiredItem")),
                Tags: tags,
                MutationExclusive: id.StartsWith("MutationPal_", StringComparison.Ordinal),
                PenaltyTags: penaltyTags,
                Effects: extractor?.Effects(id)));
        }

        extractor?.CheckPenaltyTags(passives);

        var actives = _db.GetProperty("ActiveSkills").EnumerateArray()
            .OrderBy(a => a.GetProperty("InternalName").GetString(), StringComparer.Ordinal)
            .Select(a => new ActiveDto(
                Id: a.GetProperty("InternalName").GetString()!,
                Names: Localized(a.GetProperty("LocalizedNames")),
                Element: Str(a, "ElementInternalName") ?? "",
                Power: Int(a, "Power"),
                CooldownSeconds: Math.Round(a.GetProperty("CooldownSeconds").GetDouble(), 4),
                CanInherit: a.GetProperty("CanInherit").GetBoolean(),
                HasSkillFruit: a.GetProperty("HasSkillFruit").GetBoolean()))
            .ToList();

        var elements = _db.GetProperty("Elements").EnumerateArray()
            .Select(e =>
            {
                var id = e.GetProperty("InternalName").GetString()!;
                return new ElementDto(id, Localized(e.GetProperty("LocalizedNames")), $"icons/elements/{id}.png");
            }).ToList();

        // Work type names come from the game terms (Work.<id>, en + tr) when the extractor data is present.
        var terms = extractor?.Terms();
        var workTypes = WorkTypeOrder.Select(w => new WorkTypeDto(
            w,
            terms != null && terms.TryGetValue("Work." + w, out var gameNames)
                ? new SortedDictionary<string, string>(gameNames, StringComparer.Ordinal)
                : new SortedDictionary<string, string>(StringComparer.Ordinal) { ["en"] = WorkEnglishNames[w] },
            $"icons/work/{w}.png")).ToList();

        var languages = pals.SelectMany(p => p.Names.Keys)
            .Concat(passives.SelectMany(p => p.Names.Keys))
            .Concat(actives.SelectMany(a => a.Names.Keys))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var data = new GameDataFile(
            new MetaDto(SchemaJson.CurrentSchemaVersion, "1.0.5", "25246127", generatedAtUtc,
                [new SourceDto("palcalc", commit, DbVersion)]),
            languages,
            BuildMechanics(mech),
            elements,
            workTypes,
            pals,
            passives,
            actives,
            terms,
            extractor?.Relics(),
            extractor?.Technologies());

        CopyMiscIcons(outDir);
        return (data, breeding);
    }

    private Dictionary<string, string> LoadIconOverrides()
    {
        var path = Path.Combine(_palcalcDir, "PalCalc.UI", "Resources", "PalIconOverride.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private static string EggSize(int rarity) => rarity >= 8 ? "Huge" : rarity >= 5 ? "Large" : "Normal";

    private static MechanicsDto BuildMechanics(JsonElement mech)
    {
        SortedDictionary<string, int> Weights(string name)
        {
            var d = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in mech.GetProperty(name).EnumerateObject()) d[p.Name] = p.Value.GetInt32();
            return d;
        }

        return new MechanicsDto(
            MaxPassives: 4,
            PassiveInheritWeights: Weights("PassiveInheritanceWeights"),
            PassiveRandomAddWeights: Weights("PassiveRandomWeights"),
            TalentInheritWeights: mech.GetProperty("IVInheritanceWeights").EnumerateArray().Select(x => x.GetInt32()).ToList(),
            AlphaRate: 0.05,
            Mutation: new MutationDto(0.01, 0.5, 0.4, 0.1, 90),
            WildPassiveCountWeights: [10, 76, 10, 3, 1],
            EggSizeMinRarity: new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["Normal"] = 0, ["Large"] = 5, ["Huge"] = 8,
            },
            Cakes:
            [
                new CakeDto("Cake01", 0, 0, 0, 0, 1, false, 0),
                new CakeDto("Cake02", 1, 5, 0, 0, 1, false, 0),
                new CakeDto("Cake03", 0, 0, 0, 0, 2, false, 0),
                new CakeDto("Cake04", 1, 5, 2, 0, 1, false, 0),
                new CakeDto("Cake05", 0, 0, 0, 0, 1, true, 4),
            ]);
    }

    /// <summary>
    /// Builds the upper-triangular breeding matrix. For the one pair that only has gender-specific rows
    /// (CatMage + FoxMage) the matrix holds the child of the first row in file order; GenderSpecific
    /// carries both gender-aware results.
    /// </summary>
    private BreedingFile BuildBreeding(out int[] pairCounts, out bool[] sameSpeciesOnly)
    {
        var palOrder = _db.GetProperty("Pals").EnumerateArray()
            .Select(p => p.GetProperty("InternalName").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();
        var index = palOrder.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        var n = palOrder.Count;

        int Slot(int i, int j)
        {
            if (i > j) (i, j) = (j, i);
            return i * n - i * (i - 1) / 2 + (j - i);
        }

        var matrix = new int[n * (n + 1) / 2];
        Array.Fill(matrix, -1);
        var genderSpecific = new List<GenderSpecificDto>();
        var pairsPerChild = new HashSet<(int, int)>[n];
        for (var i = 0; i < n; i++) pairsPerChild[i] = [];

        foreach (var row in _breedingRows.EnumerateArray())
        {
            var a = row.GetProperty("Parent1InternalName").GetString()!;
            var b = row.GetProperty("Parent2InternalName").GetString()!;
            var c = row.GetProperty("ChildInternalName").GetString()!;
            var ga = row.GetProperty("Parent1Gender").GetString()!;
            var gb = row.GetProperty("Parent2Gender").GetString()!;
            int ia = index[a], ib = index[b], ic = index[c];

            var slot = Slot(ia, ib);
            if (ga != "WILDCARD" || gb != "WILDCARD")
            {
                genderSpecific.Add(new GenderSpecificDto(a, GenderName(ga), b, GenderName(gb), c));
                if (matrix[slot] == -1) matrix[slot] = ic;
            }
            else
            {
                if (matrix[slot] != -1)
                    throw new InvalidDataException($"Duplicate breeding row for {a} + {b}.");
                matrix[slot] = ic;
            }

            pairsPerChild[ic].Add((Math.Min(ia, ib), Math.Max(ia, ib)));
        }

        var missing = matrix.Count(v => v == -1);
        if (missing != 0) throw new InvalidDataException($"{missing} breeding matrix slots are not covered by breeding.json.");

        pairCounts = new int[n];
        sameSpeciesOnly = new bool[n];
        for (var i = 0; i < n; i++)
        {
            pairCounts[i] = pairsPerChild[i].Count;
            sameSpeciesOnly[i] = pairsPerChild[i].Count == 1 && pairsPerChild[i].Single() is var (x, y) && x == y;
        }

        return new BreedingFile(palOrder, matrix, genderSpecific);
    }

    private static string GenderName(string g) => g switch
    {
        "MALE" => "Male",
        "FEMALE" => "Female",
        _ => throw new InvalidDataException($"Unexpected gender value {g}."),
    };

    private void CopyMiscIcons(string outDir)
    {
        var res = Path.Combine(_palcalcDir, "PalCalc.UI", "Resources");

        void CopyDir(string srcDir, string dstDir)
        {
            if (!Directory.Exists(srcDir)) { Warnings.Add($"Missing icon folder {srcDir}"); return; }
            foreach (var f in Directory.GetFiles(srcDir, "*.png").Order(StringComparer.Ordinal))
                BundleWriter.CopyIcon(f, Path.Combine(dstDir, Path.GetFileName(f)));
        }

        CopyDir(Path.Combine(res, "Elements"), Path.Combine(outDir, "icons", "elements"));
        CopyDir(Path.Combine(res, "SkillElements"), Path.Combine(outDir, "icons", "misc", "skill-elements"));
        CopyDir(Path.Combine(res, "TraitRank"), Path.Combine(outDir, "icons", "passive-rank"));

        var workFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in WorkTypeOrder)
        {
            var file = (WorkIconFile.TryGetValue(w, out var f) ? f : w) + ".png";
            workFiles.Add(file);
            var src = Path.Combine(res, file);
            if (File.Exists(src)) BundleWriter.CopyIcon(src, Path.Combine(outDir, "icons", "work", w + ".png"));
            else Warnings.Add($"Missing work icon {file}");
        }

        // Remaining top-level icons (attack, health, day/night, cakes, ...) go to misc.
        foreach (var f in Directory.GetFiles(res, "*.png").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            if (workFiles.Contains(name)) continue;
            BundleWriter.CopyIcon(f, Path.Combine(outDir, "icons", "misc", name));
        }
    }
}
