namespace Paldium.Core.Queries;

/// <summary>One active filter predicate with a stable key for active-filter chips and empty-result removal hints.</summary>
public sealed record FilterClause<T>(string Key, Func<T, bool> Predicate);

/// <summary>How many items remain matching all other active filters if one filter clause is removed.</summary>
public sealed record FilterRemovalResult(string Key, int RemainingCount);

/// <summary>
/// Evaluates active filter clauses and computes how many items remain when each single clause is removed (T84, T87).
/// </summary>
public static class FilterRemovalHelper
{
    public static int CountMatches<T>(IReadOnlyList<T> source, IReadOnlyList<FilterClause<T>> clauses)
    {
        if (clauses.Count == 0) return source.Count;

        var count = 0;
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            var allPass = true;
            for (var c = 0; c < clauses.Count; c++)
            {
                if (!clauses[c].Predicate(item))
                {
                    allPass = false;
                    break;
                }
            }
            if (allPass) count++;
        }
        return count;
    }

    public static IReadOnlyList<FilterRemovalResult> ComputeRemovals<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<FilterClause<T>> clauses)
    {
        if (clauses.Count == 0) return [];

        var counts = new int[clauses.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            var failedIndex = -1;
            var multiFail = false;

            for (var c = 0; c < clauses.Count; c++)
            {
                if (!clauses[c].Predicate(item))
                {
                    if (failedIndex >= 0)
                    {
                        multiFail = true;
                        break;
                    }
                    failedIndex = c;
                }
            }

            if (multiFail) continue;

            if (failedIndex < 0)
            {
                // Matches every clause: removing any clause leaves it included.
                for (var c = 0; c < clauses.Count; c++) counts[c]++;
            }
            else
            {
                // Fails only clause[failedIndex]: removing that clause makes it match.
                counts[failedIndex]++;
            }
        }

        var results = new FilterRemovalResult[clauses.Count];
        for (var c = 0; c < clauses.Count; c++)
            results[c] = new FilterRemovalResult(clauses[c].Key, counts[c]);

        return results;
    }
}
