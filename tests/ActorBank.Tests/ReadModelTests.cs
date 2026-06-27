namespace ActorBank.Tests;

/// <summary>
/// The balance read model (lever 1): non-transactional, post-commit projection of an account's
/// balance. These cover its contract — cold miss, publish, and the version guard that keeps it
/// converging to the newest committed balance even if publishes arrive out of order.
/// </summary>
public sealed class ReadModelTests : IClassFixture<ClusterFixture>
{
    private readonly ClusterFixture _fixture;

    public ReadModelTests(ClusterFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Cluster.GrainFactory;
    private static string NewId() => $"acct-{Guid.NewGuid():N}";

    [Fact]
    public async Task Cold_read_model_returns_null()
    {
        var rm = Grains.GetGrain<IAccountReadModelGrain>(NewId());
        Assert.Null(await rm.TryGetBalance());
    }

    [Fact]
    public async Task Publish_records_the_balance()
    {
        var rm = Grains.GetGrain<IAccountReadModelGrain>(NewId());
        await rm.Publish(new BalanceUpdate(250m, 3));
        Assert.Equal(250m, await rm.TryGetBalance());
    }

    [Fact]
    public async Task Stale_publish_is_ignored_and_newer_one_wins()
    {
        var rm = Grains.GetGrain<IAccountReadModelGrain>(NewId());
        await rm.Publish(new BalanceUpdate(100m, 5));
        await rm.Publish(new BalanceUpdate(50m, 4));  // older version → must be ignored
        Assert.Equal(100m, await rm.TryGetBalance());

        await rm.Publish(new BalanceUpdate(80m, 6));  // newer version → wins
        Assert.Equal(80m, await rm.TryGetBalance());
    }

    [Fact]
    public async Task Account_operations_return_a_monotonic_version()
    {
        var id = NewId();
        var account = Grains.GetGrain<IAccountGrain>(id);
        await account.Open("owner", 0m);

        var first = await account.Deposit(10m);
        var second = await account.Deposit(10m);

        Assert.True(second.Version > first.Version, "ledger version must increase per balance change");
        Assert.Equal(20m, second.Balance);
    }
}
