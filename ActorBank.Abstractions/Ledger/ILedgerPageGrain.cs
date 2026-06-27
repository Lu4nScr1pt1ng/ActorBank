namespace ActorBank.Abstractions.Ledger;

/// <summary>
/// One bounded, append-only page of an account's ledger (grain key: <c>"{accountId}/{page}"</c>).
/// Pages participate in the caller's Orleans transaction, so a ledger entry commits or rolls back
/// atomically with the balance change that produced it.
/// </summary>
public interface ILedgerPageGrain : IGrainWithStringKey
{
    /// <summary>Appends an entry to this page.</summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task Append(TransactionRecord entry);

    /// <summary>Reads a copy of every entry on this page, in append order.</summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<List<TransactionRecord>> Read();
}
