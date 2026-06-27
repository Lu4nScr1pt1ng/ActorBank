namespace ActorBank.Tests;

/// <summary>
/// The account → sweep-shard mapping must be deterministic and stable across processes (an account
/// must always land on the same shard, or its interest reminder would be orphaned), and it must
/// spread accounts across the shard pool.
/// </summary>
public sealed class InterestShardingTests
{
    [Theory]
    [InlineData("alice-001", 16)]
    [InlineData("bob-her-account", 4)]
    [InlineData("", 8)]
    public void ShardOf_is_in_range(string accountId, int shards)
    {
        var shard = InterestSharding.ShardOf(accountId, shards);
        Assert.InRange(shard, 0, shards - 1);
    }

    [Fact]
    public void ShardOf_is_stable_for_the_same_id()
    {
        Assert.Equal(
            InterestSharding.ShardOf("acct-stable", 16),
            InterestSharding.ShardOf("acct-stable", 16));
    }

    [Fact]
    public void ShardOf_spreads_accounts_across_shards()
    {
        const int shards = 16;
        var hit = new HashSet<long>();
        for (var i = 0; i < 2000; i++)
            hit.Add(InterestSharding.ShardOf($"acct-{i}", shards));

        // A decent hash should reach every shard over a couple thousand ids.
        Assert.Equal(shards, hit.Count);
    }

    [Fact]
    public void ShardOf_rejects_a_nonpositive_shard_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InterestSharding.ShardOf("x", 0));
    }
}
