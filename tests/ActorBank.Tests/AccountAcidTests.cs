namespace ActorBank.Tests;

/// <summary>
/// ACID / consistency tests run against a real (in-memory) Orleans cluster with the transaction
/// subsystem enabled. Transfers are composed exactly as the API composes them — one transaction
/// over two account grains via <see cref="ITransactionClient"/>, legs issued in id order.
/// </summary>
public sealed class AccountAcidTests : IClassFixture<ClusterFixture>
{
    private readonly ClusterFixture _fixture;

    public AccountAcidTests(ClusterFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Cluster.GrainFactory;
    private IAccountGrain Account(string id) => Grains.GetGrain<IAccountGrain>(id);
    private static string NewId() => $"acct-{Guid.NewGuid():N}";
    private Task OpenAsync(string id, decimal opening) => Account(id).Open("owner", opening);
    private Task<decimal> BalanceAsync(string id) => Account(id).GetBalance();

    // Each transfer runs on its own coordinator activation (fresh GUID) so concurrent transfers
    // execute in parallel — composing one transaction over both account grains, just like the API.
    private Task TransferAsync(string from, string to, decimal amount) =>
        Grains.GetGrain<ITestTransferGrain>(Guid.NewGuid()).Run(from, to, amount);

    [Fact]
    public async Task Deposit_and_withdraw_track_balance()
    {
        var id = NewId();
        await OpenAsync(id, 1000m);
        Assert.Equal(1250m, await Account(id).Deposit(250m));
        Assert.Equal(1175m, await Account(id).Withdraw(75m));
    }

    [Fact]
    public async Task Overdraw_is_rejected_and_balance_unchanged()
    {
        var id = NewId();
        await OpenAsync(id, 100m);

        await AssertThrowsInChain<InsufficientFundsException>(() => Account(id).Withdraw(500m));

        Assert.Equal(100m, await BalanceAsync(id));
    }

    [Fact]
    public async Task Transfer_moves_money_atomically()
    {
        var a = NewId();
        var b = NewId();
        await OpenAsync(a, 1000m);
        await OpenAsync(b, 0m);

        await TransferAsync(a, b, 300m);

        Assert.Equal(700m, await BalanceAsync(a));
        Assert.Equal(300m, await BalanceAsync(b));
    }

    [Fact]
    public async Task Transfer_to_unopened_account_rolls_back_the_debit()
    {
        var a = NewId();
        var ghost = NewId();
        await OpenAsync(a, 1000m);

        await Assert.ThrowsAnyAsync<Exception>(() => TransferAsync(a, ghost, 250m));

        // Atomicity: the debit must be rolled back — balance intact, no phantom ledger entry.
        Assert.Equal(1000m, await BalanceAsync(a));
        var statement = await Account(a).GetStatement(100);
        Assert.DoesNotContain(statement.Transactions, t => t.Type == TransactionType.TransferOut);
    }

    [Fact]
    public async Task Money_is_conserved_under_concurrent_transfers()
    {
        const int accounts = 8;
        const decimal initial = 1000m;
        var ids = Enumerable.Range(0, accounts).Select(_ => NewId()).ToArray();
        foreach (var id in ids) await OpenAsync(id, initial);

        var rng = new Random(7);
        var transfers = new List<Task>();
        for (var i = 0; i < 120; i++)
        {
            var from = ids[rng.Next(accounts)];
            var to = ids[rng.Next(accounts)];
            if (from == to) continue;
            var amount = rng.Next(1, 11);
            transfers.Add(SafeTransfer(from, to, amount));
        }
        await Task.WhenAll(transfers);

        decimal total = 0;
        foreach (var id in ids) total += await BalanceAsync(id);
        Assert.Equal(accounts * initial, total); // not a cent created or lost
    }

    [Fact]
    public async Task Concurrent_deposits_have_no_lost_updates()
    {
        var id = NewId();
        await OpenAsync(id, 0m);
        var account = Account(id);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => account.Deposit(1m)));

        Assert.Equal(100m, await BalanceAsync(id));
    }

    [Fact]
    public async Task Ledger_pages_across_many_transactions()
    {
        var id = NewId();
        await OpenAsync(id, 0m);
        var account = Account(id);
        for (var i = 1; i <= 130; i++) await account.Deposit(1m, $"d{i}");

        Assert.Equal(130m, await BalanceAsync(id));

        var full = await account.GetStatement(1000);
        Assert.Equal(130, full.Transactions.Count);
        Assert.Equal("d1", full.Transactions[0].Description);
        Assert.Equal("d130", full.Transactions[^1].Description);

        var recent = await account.GetStatement(50);
        Assert.Equal(50, recent.Transactions.Count);
        Assert.Equal("d130", recent.Transactions[^1].Description);
    }

    [Fact]
    public async Task Apply_interest_credits_and_records_an_entry()
    {
        var id = NewId();
        await OpenAsync(id, 1000m);
        var account = Account(id);

        Assert.Equal(1010m, await account.ApplyInterest(1m)); // 1% of 1000

        var statement = await account.GetStatement();
        Assert.Contains(statement.Transactions, t => t.Type == TransactionType.Interest && t.Amount == 10m);
    }

    private async Task SafeTransfer(string from, string to, decimal amount)
    {
        try
        {
            await TransferAsync(from, to, amount);
        }
        catch (Exception)
        {
            // Insufficient funds or a transient transaction failure — it rolled back, so the
            // conservation invariant still holds. That's the point of the test.
        }
    }

    private static async Task AssertThrowsInChain<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            for (var e = (Exception?)ex; e is not null; e = e.InnerException)
                if (e is T) return;
            throw new Xunit.Sdk.XunitException($"Expected {typeof(T).Name} in the chain, got {ex.GetType().Name}: {ex.Message}");
        }
        throw new Xunit.Sdk.XunitException($"Expected {typeof(T).Name} but nothing was thrown.");
    }
}
