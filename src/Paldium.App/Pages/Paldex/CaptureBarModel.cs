using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Queries;

namespace Paldium.App.Pages.PaldexParts;

/// <summary>What the Paldex "Capture" cell draws for one species (T121, T132): the lit segments of the five, whether it is finished (the Mimog look) and the counts for the tip.</summary>
public sealed record CaptureBar(int Bonus, int Total, bool Done)
{
    public int Segments => CaptureRecord.MaxBonus;

    /// <summary>Nothing caught yet.</summary>
    public bool None => Bonus == 0 && Total == 0;

    /// <summary>"3/5": the number beside the bar (a finished bar shows the Mimog icon in front of "5/5").</summary>
    public string Label => $"{Bonus}/{Segments}";
}

/// <summary>Pure functions behind the Paldex capture cell and the summary line, tested without the page.</summary>
public static class CaptureBarModel
{
    public static CaptureBar Of(CaptureRecord record, string speciesId)
    {
        var bonus = record.BonusOf(speciesId);
        return new CaptureBar(bonus, Math.Max(record.CountOf(speciesId), bonus), bonus >= CaptureRecord.MaxBonus);
    }

    /// <summary>The species that are finished (5/5) out of the ones that can be caught: "2/299".</summary>
    public static (int Done, int Capturable) Summary(CaptureRecord record, IEnumerable<PalSpecies> species)
    {
        var capturable = species.Where(PalQuery.CountsForCapture).ToList();
        return (capturable.Count(p => record.BonusOf(p.Id) >= CaptureRecord.MaxBonus), capturable.Count);
    }
}
