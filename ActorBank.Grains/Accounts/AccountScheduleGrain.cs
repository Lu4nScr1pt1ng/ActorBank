using Microsoft.Extensions.Logging;

namespace ActorBank.Grains.Accounts;

/// <summary>
/// Holds the durable interest reminder for one account (grain key = account id). Keeping the
/// reminder here — not on the <see cref="AccountGrain"/> — means the tick can call the account as a
/// normal grain-to-grain message, which is safe; a grain calling itself would deadlock.
/// </summary>
public sealed class AccountScheduleGrain : Grain, IAccountScheduleGrain, IRemindable
{
    private const string InterestReminder = "interest";

    private readonly IPersistentState<AccountScheduleState> _state;
    private readonly ILogger<AccountScheduleGrain> _logger;

    public AccountScheduleGrain(
        [PersistentState("schedule", "accountStore")] IPersistentState<AccountScheduleState> state,
        ILogger<AccountScheduleGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    private string AccountId => this.GetPrimaryKeyString();

    public async Task EnsureInterestSchedule(double periodMinutes, decimal ratePercentPerPeriod)
    {
        _state.State.RatePercent = ratePercentPerPeriod;
        _state.State.Scheduled = true;
        await _state.WriteStateAsync();

        // Orleans enforces a one-minute minimum period.
        var period = TimeSpan.FromMinutes(Math.Max(1, periodMinutes));
        await this.RegisterOrUpdateReminder(InterestReminder, period, period);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != InterestReminder)
            return;

        var rate = _state.State.RatePercent;
        if (rate <= 0)
            return;

        var balance = await GrainFactory.GetGrain<IAccountGrain>(AccountId).ApplyInterest(rate);
        AccountScheduleLog.InterestApplied(_logger, AccountId, rate, balance);
    }
}

/// <summary>Source-generated log messages for <see cref="AccountScheduleGrain"/>.</summary>
internal static partial class AccountScheduleLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {rate}% interest to '{accountId}' -> balance {balance}")]
    public static partial void InterestApplied(ILogger logger, string accountId, decimal rate, decimal balance);
}
