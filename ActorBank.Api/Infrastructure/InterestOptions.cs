namespace ActorBank.Api.Infrastructure;

/// <summary>Configuration for scheduled interest (bound from the "Interest" section).</summary>
public sealed class InterestOptions
{
    /// <summary>How often interest is applied. Orleans enforces a one-minute minimum.</summary>
    public double PeriodMinutes { get; set; } = 60;

    /// <summary>Interest rate (percent of balance) credited on each tick.</summary>
    public decimal RatePercentPerPeriod { get; set; } = 1.0m;

    /// <summary>
    /// Number of interest-sweep coordinator grains. Accounts are spread across these shards so a
    /// fixed, small set of durable reminders covers every account — instead of one reminder each.
    /// </summary>
    public int SweepShards { get; set; } = 16;
}
