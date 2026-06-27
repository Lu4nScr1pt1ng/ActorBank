namespace ActorBank.Grains.Accounts;

/// <summary>
/// Durable state for one interest-sweep shard: the rate to credit and the set of account ids the
/// shard is responsible for. The set is rewritten on each enrollment, so a shard is intended to
/// hold a bounded slice of the account space — increase the shard count to keep each slice small.
/// </summary>
[GenerateSerializer]
public sealed class InterestSweepState
{
    [Id(0)] public decimal RatePercent { get; set; }
    [Id(1)] public HashSet<string> Accounts { get; set; } = [];
}
