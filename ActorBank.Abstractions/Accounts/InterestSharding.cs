using System.Text;

namespace ActorBank.Abstractions.Accounts;

/// <summary>
/// Maps an account id to one of a fixed number of <see cref="IInterestSweepGrain"/> shards. The
/// mapping must be stable across processes and restarts (an account must always land on the same
/// shard), so it uses FNV-1a rather than <see cref="string.GetHashCode()"/>, which is randomized
/// per process.
/// </summary>
public static class InterestSharding
{
    /// <summary>The shard (sweep-grain key) that owns interest for the given account.</summary>
    public static long ShardOf(string accountId, int shardCount)
    {
        if (shardCount < 1)
            throw new ArgumentOutOfRangeException(nameof(shardCount), shardCount, "Shard count must be positive.");

        // FNV-1a 32-bit over the UTF-8 bytes — deterministic across runs and silos.
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(accountId))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash % (uint)shardCount;
    }
}
