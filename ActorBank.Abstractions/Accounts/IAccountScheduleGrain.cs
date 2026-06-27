namespace ActorBank.Abstractions.Accounts;

/// <summary>
/// Owns the recurring schedule for one account (grain key = account id). It holds the Orleans
/// reminder so the work runs on a <em>separate</em> grain from the account — the reminder tick
/// then calls the account, avoiding a re-entrant self-call.
/// </summary>
public interface IAccountScheduleGrain : IGrainWithStringKey
{
    /// <summary>Registers (or updates) the periodic interest reminder for this account.</summary>
    Task EnsureInterestSchedule(double periodMinutes, decimal ratePercentPerPeriod);
}
