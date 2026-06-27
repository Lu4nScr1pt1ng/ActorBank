namespace ActorBank.Grains.Accounts;

/// <summary>
/// Hot, transactional state for an <see cref="AccountGrain"/>. Deliberately small and
/// constant-size: the full history lives in append-only ledger pages, not here, so the
/// ACID money path serializes the same few fields no matter how active the account is.
/// </summary>
[GenerateSerializer]
public sealed class AccountState
{
    [Id(0)] public bool IsOpen { get; set; }
    [Id(1)] public string Owner { get; set; } = "";
    [Id(2)] public decimal Balance { get; set; }

    /// <summary>Total number of ledger entries ever appended; drives page placement.</summary>
    [Id(3)] public long LedgerCount { get; set; }

    /// <summary>
    /// Entries of the latest, still-filling ledger page, co-located with the balance so a deposit or
    /// withdrawal is a single-participant transaction (no separate ledger-page grain in the 2PC). When
    /// this reaches <see cref="ActorBank.Abstractions.Ledger.LedgerPaging.PageSize"/> it is flushed to
    /// an archive page grain — in the same transaction — and reset. Its count is always
    /// <c>LedgerCount % PageSize</c>.
    /// </summary>
    [Id(4)] public List<TransactionRecord> CurrentPage { get; set; } = [];
}
