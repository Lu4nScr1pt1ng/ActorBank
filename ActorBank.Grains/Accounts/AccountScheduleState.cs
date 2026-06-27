namespace ActorBank.Grains.Accounts;

/// <summary>Persisted schedule settings: the interest rate applied on each reminder tick.</summary>
[GenerateSerializer]
public sealed class AccountScheduleState
{
    [Id(0)] public decimal RatePercent { get; set; }
    [Id(1)] public bool Scheduled { get; set; }
}
