namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// The fixed latency buckets every traffic row is counted into.
/// </summary>
/// <remarks>
/// <para>
/// <b>A histogram rather than a mean, and that is the whole point.</b> Means cannot be combined
/// across pods into a percentile, and p95 is the number an operator actually wants; counts in
/// fixed buckets sum across pods trivially and interpolate into one. ADR 0021.
/// </para>
/// <para>
/// <b>The boundaries are a stored contract.</b> Rows already written were counted against THESE
/// edges, so changing one silently changes what every existing row means — the same hazard the
/// enums-as-names rule guards against elsewhere. Adding a bucket means a migration and a new
/// column, not an edit here.
/// </para>
/// </remarks>
public static class TrafficHistogram
{
    /// <summary>Upper bounds in milliseconds, ascending. A value at the bound falls in that bucket.</summary>
    public static readonly long[] Bounds = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000];

    /// <summary>One more than <see cref="Bounds"/>: the last counts everything slower than the top bound.</summary>
    public static int BucketCount => Bounds.Length + 1;

    /// <summary>Which bucket a duration belongs in.</summary>
    public static int IndexFor(long elapsedMs)
    {
        for (var index = 0; index < Bounds.Length; index++)
        {
            if (elapsedMs <= Bounds[index])
            {
                return index;
            }
        }

        return Bounds.Length;
    }

    /// <summary>
    /// The percentile interpolated from summed bucket counts.
    /// </summary>
    /// <remarks>
    /// Linear interpolation WITHIN the bucket the percentile falls in, between that bucket's lower
    /// and upper bound — the standard approach, and approximate by construction. The overflow
    /// bucket has no upper bound, so a percentile landing there reports the top bound: "at least
    /// this slow" is the honest answer, and inventing a number above it would not be.
    /// </remarks>
    public static double? Percentile(IReadOnlyList<long> counts, double percentile)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var total = counts.Sum();
        if (total == 0)
        {
            return null;
        }

        var target = total * percentile;
        long cumulative = 0;

        for (var index = 0; index < counts.Count; index++)
        {
            var before = cumulative;
            cumulative += counts[index];

            if (cumulative < target)
            {
                continue;
            }

            if (index >= Bounds.Length)
            {
                return Bounds[^1];
            }

            var lower = index == 0 ? 0d : Bounds[index - 1];
            var upper = (double)Bounds[index];

            // How far into this bucket the target falls. A bucket holding the target alone puts
            // it at the bucket's own upper bound rather than dividing by zero.
            var within = counts[index] == 0 ? 1d : (target - before) / counts[index];

            return lower + ((upper - lower) * Math.Clamp(within, 0d, 1d));
        }

        return Bounds[^1];
    }
}
