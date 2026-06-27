namespace ActorBank.Abstractions.Accounts;

/// <summary>
/// A coordinator that owns the interest schedule for a <em>shard</em> of accounts (grain key =
/// shard index). Instead of one durable reminder per account — which fans out to millions of
/// reminders and a periodic load spike — a small, fixed pool of these grains each hold a single
/// reminder and sweep their enrolled accounts on each tick, calling
/// <see cref="IAccountGrain.ApplyInterest"/> as an ordinary grain-to-grain message.
/// </summary>
public interface IInterestSweepGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Enrolls an account into this shard's interest sweep and ensures the shard's reminder is
    /// running. Idempotent: re-enrolling an account is a no-op beyond refreshing the rate/period.
    /// </summary>
    Task Enroll(string accountId, decimal ratePercentPerPeriod, double periodMinutes);
}
