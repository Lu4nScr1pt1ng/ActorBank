namespace ActorBank.Abstractions.Models;

/// <summary>
/// The result of a balance-changing operation: the new balance and a monotonic <see cref="Version"/>
/// (the account's ledger count after the op). The version lets the read model ignore a stale publish
/// that races ahead of a newer one.
/// </summary>
[GenerateSerializer]
public record BalanceUpdate(
    [property: Id(0)] decimal Balance,
    [property: Id(1)] long Version);
