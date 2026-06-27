namespace ActorBank.Grains.Accounts;

/// <summary>Persisted read-model state: the last committed balance and the version that produced it.</summary>
[GenerateSerializer]
public sealed class AccountReadModelState
{
    [Id(0)] public decimal Balance { get; set; }
    [Id(1)] public long Version { get; set; }
    [Id(2)] public bool Known { get; set; }
}
