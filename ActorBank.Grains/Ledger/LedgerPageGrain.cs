namespace ActorBank.Grains.Ledger;

/// <summary>
/// A bounded, append-only page of an account's ledger. Each instance holds at most
/// <see cref="LedgerPaging.PageSize"/> entries, so an append rewrites only this page —
/// not the account's entire history. State is transactional, so entries commit/roll back
/// together with the balance change that produced them.
/// </summary>
public sealed class LedgerPageGrain : Grain, ILedgerPageGrain
{
    private readonly ITransactionalState<LedgerPageState> _page;

    public LedgerPageGrain(
        [TransactionalState("page", "ledgerStore")] ITransactionalState<LedgerPageState> page)
    {
        _page = page;
    }

    public Task Append(TransactionRecord entry) =>
        _page.PerformUpdate(s => s.Entries.Add(entry));

    public Task<List<TransactionRecord>> Read() =>
        _page.PerformRead(s => new List<TransactionRecord>(s.Entries));
}
