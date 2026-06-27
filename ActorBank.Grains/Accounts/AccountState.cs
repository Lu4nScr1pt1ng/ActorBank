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
}
