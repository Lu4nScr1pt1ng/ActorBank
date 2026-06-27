namespace ActorBank.Grains.Ledger;

/// <summary>Transactional state for a single ledger page: a bounded list of entries.</summary>
[GenerateSerializer]
public sealed class LedgerPageState
{
    [Id(0)] public List<TransactionRecord> Entries { get; set; } = [];
}
