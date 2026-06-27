namespace ActorBank.Abstractions.Ledger;

/// <summary>
/// One bounded, append-only page of an account's ledger (grain key: <c>"{accountId}/{page}"</c>).
/// Pages participate in the caller's Orleans transaction, so a ledger entry commits or rolls back
/// atomically with the balance change that produced it.
/// </summary>
public interface ILedgerPageGrain : IGrainWithStringKey
{
    /// <summary>
    /// Writes a completed page in one shot. The account grain accumulates the current page in its own
    /// state and flushes it here (within the same transaction) once full, so a page is written exactly
    /// once and the common write path never touches this grain.
    /// </summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task Write(IReadOnlyList<TransactionRecord> entries);

    /// <summary>Reads a copy of every entry on this page, in append order.</summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<List<TransactionRecord>> Read();
}
