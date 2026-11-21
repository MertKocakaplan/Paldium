namespace Paldium.Core.Planning;

/// <summary>Checks plan trees: a step needs two different Pals.</summary>
public static class PlanTreeValidator
{
    /// <summary>Every leaf of the tree, left to right.</summary>
    public static IEnumerable<PlanNode> Leaves(PlanNode node)
    {
        if (node is BreedStep step)
        {
            foreach (var leaf in Leaves(step.ParentA)) yield return leaf;
            foreach (var leaf in Leaves(step.ParentB)) yield return leaf;
        }
        else
        {
            yield return node;
        }
    }

    /// <summary>
    /// True when no step has the same owned Pal as both parents. One Pal may serve several steps: breeding does not use a parent up and the
    /// steps run one after the other (ARCHITECTURE 7.3).
    /// </summary>
    public static bool IsValid(PlanNode root) =>
        root is not BreedStep step ||
        (!(step.ParentA is OwnedLeaf a && step.ParentB is OwnedLeaf b && a.Pal.Uid == b.Pal.Uid) &&
         IsValid(step.ParentA) && IsValid(step.ParentB) && (step.AltPartner == null || IsValid(step.AltPartner)));

    public static int CountSteps(PlanNode node) =>
        node is BreedStep step ? 1 + CountSteps(step.ParentA) + CountSteps(step.ParentB) : 0;
}
