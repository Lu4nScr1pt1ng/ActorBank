namespace ActorBank.Abstractions.Ledger;

/// <summary>
/// Sharding rules for an account's append-only ledger. History is split into fixed-size pages,
/// each stored in its own <see cref="ILedgerPageGrain"/>, so appending an entry only ever
/// rewrites the current page (bounded cost) instead of the whole history.
/// </summary>
public static class LedgerPaging
{
    /// <summary>Maximum entries per ledger page.</summary>
    public const int PageSize = 128;

    /// <summary>The page number that holds the entry at the given zero-based global index.</summary>
    public static long PageOf(long entryIndex) => entryIndex / PageSize;

    /// <summary>Grain key for a given account's ledger page.</summary>
    public static string PageKey(string accountId, long page) => $"{accountId}/{page}";
}
