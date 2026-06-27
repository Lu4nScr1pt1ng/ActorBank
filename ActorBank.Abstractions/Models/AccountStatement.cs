namespace ActorBank.Abstractions.Models;

/// <summary>A point-in-time snapshot of an account: owner, balance and full history.</summary>
[GenerateSerializer]
public record AccountStatement(
    [property: Id(0)] string AccountId,
    [property: Id(1)] string Owner,
    [property: Id(2)] decimal Balance,
    [property: Id(3)] IReadOnlyList<TransactionRecord> Transactions);
