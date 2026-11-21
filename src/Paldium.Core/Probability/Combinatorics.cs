namespace Paldium.Core.Probability;

/// <summary>Small exact binomial coefficients (pools hold at most a handful of passives).</summary>
public static class Combinatorics
{
    /// <summary>C(n, k) as a double; 0 when k is outside 0..n (also for negative arguments).</summary>
    public static double Choose(int n, int k)
    {
        if (n < 0 || k < 0 || k > n) return 0;
        k = Math.Min(k, n - k);
        double result = 1;
        for (var i = 1; i <= k; i++) result = result * (n - k + i) / i;
        return Math.Round(result);
    }
}
