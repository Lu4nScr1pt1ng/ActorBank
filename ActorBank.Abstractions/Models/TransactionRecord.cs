namespace ActorBank.Abstractions.Models;

/// <summary>
/// An immutable ledger entry. <see cref="Amount"/> is always the absolute value of the
/// movement; <see cref="BalanceAfter"/> is the resulting balance once it was applied.
/// </summary>
[GenerateSerializer]
public record TransactionRecord(
    [property: Id(0)] DateTimeOffset Timestamp,
    [property: Id(1)] TransactionType Type,
    [property: Id(2)] decimal Amount,
    [property: Id(3)] decimal BalanceAfter,
    [property: Id(4)] string? Description);
