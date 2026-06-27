namespace ActorBank.Abstractions.Accounts;

/// <summary>
/// A non-transactional read model of one account's balance (grain key = account id). It is updated
/// <em>post-commit</em> by whoever ran a money operation, so it only ever reflects committed state —
/// and it serves <c>GetBalance</c> without opening a transaction, keeping balance reads off the
/// (coordinator-bound) money path. It is eventually consistent: the authoritative balance always lives
/// on <see cref="IAccountGrain"/>, which still backs the overdraft check.
/// </summary>
public interface IAccountReadModelGrain : IGrainWithStringKey
{
    /// <summary>Records a committed balance. Ignores an update older than the latest seen version.</summary>
    Task Publish(BalanceUpdate update);

    /// <summary>The last published balance, or null if none has been published yet.</summary>
    Task<decimal?> TryGetBalance();
}
