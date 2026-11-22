using Paldium.Core.Domain;

namespace Paldium.Core.Planning;

/// <summary>Checks plan trees: a step needs two different Pals.</summary>
public static class PlanTreeValidator
{
    /// <summary>Every leaf of the tree, left to right (including flex backup partners).</summary>
    public static IEnumerable<PlanNode> Leaves(PlanNode node)
    {
        if (node is BreedStep step)
        {
            foreach (var leaf in Leaves(step.ParentA)) yield return leaf;
            foreach (var leaf in Leaves(step.ParentB)) yield return leaf;
            if (step.AltPartner != null)
                foreach (var leaf in Leaves(step.AltPartner)) yield return leaf;
        }
        else
        {
            yield return node;
        }
    }

    /// <summary>Counts wild leaves required along a plan without double-counting an alternative backup partner of the same partner branch.</summary>
    public static int CountWild(PlanNode node)
    {
        if (node is WildLeaf) return 1;
        if (node is not BreedStep step) return 0;
        if (step.AltPartner == null) return CountWild(step.ParentA) + CountWild(step.ParentB);
        var flexIsA = step.ParentA is BreedStep { ChildGender: GenderReq.Any };
        var flexParent = flexIsA ? step.ParentA : step.ParentB;
        var mainPartner = flexIsA ? step.ParentB : step.ParentA;
        return CountWild(flexParent) + Math.Max(CountWild(mainPartner), CountWild(step.AltPartner));
    }

    /// <summary>
    /// True when no step has the same owned Pal as both parents (or as its flex backup partner). One Pal may serve several steps: breeding does not
    /// use a parent up and the steps run one after the other (ARCHITECTURE 7.3).
    /// </summary>
    public static bool IsValid(PlanNode root) =>
        root is not BreedStep step ||
        (!(step.ParentA is OwnedLeaf a && step.ParentB is OwnedLeaf b && a.Pal.Uid == b.Pal.Uid) &&
         !(step.AltPartner is OwnedLeaf alt &&
           ((step.ParentA is OwnedLeaf pa && pa.Pal.Uid == alt.Pal.Uid) ||
            (step.ParentB is OwnedLeaf pb && pb.Pal.Uid == alt.Pal.Uid))) &&
         IsValid(step.ParentA) && IsValid(step.ParentB) && (step.AltPartner == null || IsValid(step.AltPartner)));

    public static int CountSteps(PlanNode node) =>
        node is BreedStep step ? 1 + CountSteps(step.ParentA) + CountSteps(step.ParentB) : 0;
}
