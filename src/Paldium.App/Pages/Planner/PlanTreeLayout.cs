using System.Globalization;
using Paldium.Core.Domain;
using Paldium.Core.Planning;

namespace Paldium.App.Pages.PlannerParts;

/// <summary>Left-to-right 16px-grid tree coordinates and orthogonal rounded SVG edges for a <see cref="BreedingPlan"/>.</summary>
public sealed class PlanTreeLayout
{
    public const int NodeWidth = 228;
    public const int TargetWidth = 264;
    public const int TargetHeight = 336;
    public const int FirstColumnX = 24;
    public const int ColumnPitch = 336;
    public const int TargetOffset = 320;
    public const int Grid = 16;
    private const int VerticalGap = 16;
    private const int CurveRadius = 18;
    private const int EggHalfHeight = 18;
    private const int EggHalfWidth = 14;

    /// <summary>A step whose child can be either gender (T105): on the partner node, "if the <see cref="Flexible"/> Pal is the other gender: <see cref="Alt"/>".</summary>
    public sealed record FlexNote(PlanNode Flexible, PlanNode Alt);

    public sealed record PositionedNode(PlanNode Node, int X, int Y, int StepNumber, bool Current, bool Gone, FlexNote? Flex = null);

    /// <summary>The extra line of a partner node with a backup partner: 8px gap, 1px rule, 20px row.</summary>
    public const int FlexRowHeight = 29;
    public const int AltLineHeight = 18;
    public sealed record PositionedJoin(int X, int Y, double Eggs, bool Current, int StepNumber);
    public sealed record EdgePath(string D, bool Current, bool Ghost = false);

    public IReadOnlyList<PositionedNode> Nodes { get; private init; } = [];
    public IReadOnlyList<PositionedJoin> Joins { get; private init; } = [];
    public IReadOnlyList<EdgePath> Edges { get; private init; } = [];
    public int TargetX { get; private init; } = 360;
    public int TargetY { get; private init; } = 48;
    public int TotalWidth { get; private init; } = 976;
    public int TotalHeight { get; private init; } = 520;
    public int BottomY { get; private init; } = 480;

    /// <param name="altLineNodes">Wild nodes with a third line, "or N more species" (T114): they are taller.</param>
    public static PlanTreeLayout Build(BreedingPlan plan, int currentStepIndex, IReadOnlySet<string>? goneUids = null, ISet<PlanNode>? altLineNodes = null)
    {
        if (plan.Root is not BreedStep rootStep)
        {
            return new PlanTreeLayout
            {
                TargetX = FirstColumnX + ColumnPitch,
                TargetY = 96,
                TotalWidth = 720,
                TotalHeight = 480,
                BottomY = 448,
            };
        }

        var stepIndices = new Dictionary<BreedStep, int>(ReferenceEqualityComparer.Instance);
        var counter = 0;
        void IndexSteps(PlanNode node)
        {
            if (node is not BreedStep s) return;
            IndexSteps(s.ParentA);
            IndexSteps(s.ParentB);
            stepIndices[s] = counter++;
        }
        IndexSteps(rootStep);

        var maxDepth = DepthOf(rootStep);
        var targetX = maxDepth switch
        {
            <= 1 => FirstColumnX + ColumnPitch,
            2 => FirstColumnX + ColumnPitch + TargetOffset,
            _ => FirstColumnX + (maxDepth - 1) * ColumnPitch + TargetOffset,
        };

        int ColumnX(int col) => col >= maxDepth ? targetX : FirstColumnX + col * ColumnPitch;
        int SnapY(int y) => Math.Max(Grid, ((y + Grid / 2) / Grid) * Grid);
        bool IsGone(PlanNode n) => n is OwnedLeaf o && goneUids != null && goneUids.Contains(o.Pal.Uid);

        var flexNotes = new Dictionary<PlanNode, FlexNote>(ReferenceEqualityComparer.Instance);
        void FindFlex(PlanNode n)
        {
            if (n is not BreedStep s) return;
            if (s.AltPartner != null)
            {
                var flexible = s.ParentA is BreedStep { ChildGender: GenderReq.Any } ? s.ParentA : s.ParentB;
                var partner = ReferenceEquals(flexible, s.ParentA) ? s.ParentB : s.ParentA;
                flexNotes[partner] = new FlexNote(flexible, s.AltPartner);
            }

            FindFlex(s.ParentA);
            FindFlex(s.ParentB);
        }
        FindFlex(rootStep);

        int EstimateHeight(PlanNode n)
        {
            var passives = n switch
            {
                OwnedLeaf o => o.Pal.Passives.Count,
                BreedStep s => s.TargetPassives.Count,
                _ => 0,
            };
            var passiveH = passives > 0 ? 9 + passives * 22 + (passives - 1) * 3 : 0;
            var goneH = IsGone(n) ? 24 : 0;
            return 24 + 40 + 9 + 34 + passiveH + goneH + (flexNotes.ContainsKey(n) ? FlexRowHeight : 0) + (altLineNodes?.Contains(n) == true ? AltLineHeight : 0);
        }

        var nodes = new List<PositionedNode>();
        var joins = new List<PositionedJoin>();
        var edges = new List<EdgePath>();
        var colBottom = new int[maxDepth];

        int PlaceNode(PlanNode node, int col, bool isCurrentParent)
        {
            var x = ColumnX(col);
            if (node is not BreedStep step)
            {
                var h = EstimateHeight(node);
                var top = SnapY(colBottom[col] > 0 ? colBottom[col] + VerticalGap : Grid);
                colBottom[col] = top + h;
                nodes.Add(new PositionedNode(node, x, top, 0, isCurrentParent, IsGone(node), flexNotes.GetValueOrDefault(node)));
                return top + h / 2;
            }

            var stepIdx = stepIndices[step];
            var isThisStepCurrent = stepIdx == currentStepIndex;

            var yA = PlaceNode(step.ParentA, col - 1, isThisStepCurrent);
            var yB = PlaceNode(step.ParentB, col - 1, isThisStepCurrent);

            var parentRightX = ColumnX(col - 1) + NodeWidth;
            var joinX = (parentRightX + x) / 2;
            var joinY = (yA + yB) / 2;

            var hStep = EstimateHeight(step);
            var desiredTop = joinY - hStep / 2;
            var minColTop = colBottom[col] > 0 ? colBottom[col] + VerticalGap : Grid;
            var topStep = SnapY(Math.Max(desiredTop, minColTop));
            colBottom[col] = topStep + hStep;
            var yStep = topStep + hStep / 2;

            nodes.Add(new PositionedNode(step, x, topStep, stepIdx + 1, isCurrentParent, false, flexNotes.GetValueOrDefault(step)));

            joins.Add(new PositionedJoin(joinX, joinY, step.ExpectedEggs, isThisStepCurrent, stepIdx + 1));
            edges.Add(new EdgePath(ParentToJoinPath(parentRightX, yA, joinX, joinY), isThisStepCurrent, IsGone(step.ParentA)));
            edges.Add(new EdgePath(ParentToJoinPath(parentRightX, yB, joinX, joinY), isThisStepCurrent, IsGone(step.ParentB)));
            edges.Add(new EdgePath(JoinToChildPath(joinX + EggHalfWidth, joinY, x, yStep), isThisStepCurrent));

            return yStep;
        }

        var rootStepIdx = stepIndices[rootStep];
        var rootIsCurrent = rootStepIdx == currentStepIndex;
        var rootYA = PlaceNode(rootStep.ParentA, maxDepth - 1, rootIsCurrent);
        var rootYB = PlaceNode(rootStep.ParentB, maxDepth - 1, rootIsCurrent);

        var rootParentRightX = ColumnX(maxDepth - 1) + NodeWidth;
        var rootJoinX = (rootParentRightX + targetX) / 2;
        var rootJoinY = (rootYA + rootYB) / 2;

        var targetY = SnapY(rootJoinY - TargetHeight / 2);
        var targetInY = targetY + TargetHeight / 2;

        joins.Add(new PositionedJoin(rootJoinX, rootJoinY, rootStep.ExpectedEggs, rootIsCurrent, rootStepIdx + 1));
        edges.Add(new EdgePath(ParentToJoinPath(rootParentRightX, rootYA, rootJoinX, rootJoinY), rootIsCurrent, IsGone(rootStep.ParentA)));
        edges.Add(new EdgePath(ParentToJoinPath(rootParentRightX, rootYB, rootJoinX, rootJoinY), rootIsCurrent, IsGone(rootStep.ParentB)));
        edges.Add(new EdgePath(JoinToChildPath(rootJoinX + EggHalfWidth, rootJoinY, targetX, targetInY), rootIsCurrent));

        var maxBottom = Math.Max(colBottom.Max(), targetY + TargetHeight);
        return new PlanTreeLayout
        {
            Nodes = nodes,
            Joins = joins,
            Edges = edges,
            TargetX = targetX,
            TargetY = targetY,
            TotalWidth = targetX + TargetWidth + 24,
            TotalHeight = maxBottom + 24,
            BottomY = maxBottom,
        };
    }

    private static int DepthOf(PlanNode node) => node switch
    {
        BreedStep s => 1 + Math.Max(DepthOf(s.ParentA), DepthOf(s.ParentB)),
        _ => 0,
    };

    private static string ParentToJoinPath(int xStart, int yStart, int joinX, int joinY)
    {
        var dy = joinY - yStart;
        if (Math.Abs(dy) <= 4)
            return FormattableString.Invariant($"M{xStart} {yStart} H{joinX - EggHalfWidth}");

        var sign = dy > 0 ? 1 : -1;
        var r = Math.Min(CurveRadius, Math.Min(Math.Abs(joinX - xStart) / 2, Math.Abs(dy) / 2));
        var elbowY = yStart + sign * r;
        var endY = joinY - sign * EggHalfHeight;
        if (sign > 0 && endY < elbowY) endY = elbowY;
        if (sign < 0 && endY > elbowY) endY = elbowY;

        return string.Create(CultureInfo.InvariantCulture,
            $"M{xStart} {yStart} H{joinX - r} Q{joinX} {yStart} {joinX} {elbowY} V{endY}");
    }

    private static string JoinToChildPath(int xStart, int yStart, int xEnd, int yEnd)
    {
        if (Math.Abs(yEnd - yStart) <= 2)
            return FormattableString.Invariant($"M{xStart} {yStart} H{xEnd}");

        var midX = (xStart + xEnd) / 2;
        var sign = yEnd > yStart ? 1 : -1;
        var r = Math.Min(12, Math.Min((xEnd - xStart) / 2, Math.Abs(yEnd - yStart) / 2));
        return string.Create(CultureInfo.InvariantCulture,
            $"M{xStart} {yStart} H{midX - r} Q{midX} {yStart} {midX} {yStart + sign * r} V{yEnd - sign * r} Q{midX} {yEnd} {midX + r} {yEnd} H{xEnd}");
    }
}
