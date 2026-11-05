using System.Text;

namespace CreditUnion.Batch.TestData;

/// <summary>
/// SplitMix64 pseudo-random generator (Steele, Lea and Flood, 2014). Implemented here rather than
/// using <see cref="Random"/> so the synthetic data is defined by this file alone: identical on every
/// .NET version and platform, and reproducible in any language. Integers are drawn without modulo
/// bias. <see cref="Fork"/> derives an independent stream per label, so adding data to one part of a
/// scenario does not shift the values of another.
/// </summary>
public sealed class DeterministicRandom(ulong seed)
{
    private readonly ulong rootSeed = seed;
    private ulong state = seed;

    public ulong NextUInt64()
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    public long NextInt64(long minInclusive, long maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxExclusive, minInclusive);
        var range = (ulong)(maxExclusive - minInclusive);
        var limit = ulong.MaxValue - (ulong.MaxValue % range);
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value >= limit);

        return minInclusive + (long)(value % range);
    }

    public int Next(int minInclusive, int maxExclusive) => (int)NextInt64(minInclusive, maxExclusive);

    /// <summary>True with probability <paramref name="percent"/>/100.</summary>
    public bool Chance(int percent) => Next(0, 100) < percent;

    public T Pick<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items[Next(0, items.Count)];
    }

    public T PickWeighted<T>(IReadOnlyList<(T Item, int Weight)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var roll = Next(0, items.Sum(i => i.Weight));
        foreach (var (item, weight) in items)
        {
            if (roll < weight)
            {
                return item;
            }

            roll -= weight;
        }

        throw new InvalidOperationException("Weights must be positive.");
    }

    /// <summary>Fisher-Yates shuffle into a new list.</summary>
    public List<T> Shuffle<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Next(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    /// <summary>An independent stream derived from this stream's seed and <paramref name="label"/> (FNV-1a).</summary>
    public DeterministicRandom Fork(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(label))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return new DeterministicRandom(new DeterministicRandom(rootSeed ^ hash).NextUInt64());
    }
}
