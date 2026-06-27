using Microsoft.Extensions.Logging;

namespace ActorBank.Grains.Accounts;

/// <summary>
/// Owns the interest schedule for one shard of accounts (grain key = shard index). A single durable
/// reminder per shard ticks periodically and credits interest to every enrolled account, replacing
/// the old one-reminder-per-account fan-out. Each tick calls <see cref="IAccountGrain.ApplyInterest"/>
/// as a normal grain-to-grain message (the account does its own transactional credit), with bounded
/// concurrency so a large shard spreads its work without flooding the cluster.
/// </summary>
public sealed class InterestSweepGrain : Grain, IInterestSweepGrain, IRemindable
{
    private const string SweepReminder = "interest-sweep";

    /// <summary>How many accounts in a shard are credited concurrently per tick.</summary>
    private const int Fanout = 32;

    private readonly IPersistentState<InterestSweepState> _state;
    private readonly ILogger<InterestSweepGrain> _logger;

    public InterestSweepGrain(
        [PersistentState("interestSweep", "accountStore")] IPersistentState<InterestSweepState> state,
        ILogger<InterestSweepGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    private long Shard => this.GetPrimaryKeyLong();

    public async Task Enroll(string accountId, decimal ratePercentPerPeriod, double periodMinutes)
    {
        var rateChanged = _state.State.RatePercent != ratePercentPerPeriod;
        var added = _state.State.Accounts.Add(accountId);
        if (added || rateChanged)
        {
            _state.State.RatePercent = ratePercentPerPeriod;
            await _state.WriteStateAsync();
        }

        // Orleans enforces a one-minute minimum period.
        var period = TimeSpan.FromMinutes(Math.Max(1, periodMinutes));
        await this.RegisterOrUpdateReminder(SweepReminder, period, period);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != SweepReminder)
            return;

        var rate = _state.State.RatePercent;
        if (rate <= 0 || _state.State.Accounts.Count == 0)
            return;

        var accounts = _state.State.Accounts.ToArray();
        var credited = 0;
        for (var i = 0; i < accounts.Length; i += Fanout)
        {
            var batch = accounts.Skip(i).Take(Fanout).Select(id => CreditOne(id, rate));
            credited += (await Task.WhenAll(batch)).Count(applied => applied);
        }

        InterestSweepLog.SweepCompleted(_logger, Shard, credited, accounts.Length);
    }

    /// <summary>Credits one account, swallowing per-account failures so one bad account can't abort the sweep.</summary>
    private async Task<bool> CreditOne(string accountId, decimal rate)
    {
        try
        {
            await GrainFactory.GetGrain<IAccountGrain>(accountId).ApplyInterest(rate);
            return true;
        }
        catch (Exception ex)
        {
            InterestSweepLog.AccountFailed(_logger, accountId, ex);
            return false;
        }
    }
}

/// <summary>Source-generated log messages for <see cref="InterestSweepGrain"/>.</summary>
internal static partial class InterestSweepLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Interest sweep on shard {shard} credited {credited}/{total} accounts")]
    public static partial void SweepCompleted(ILogger logger, long shard, int credited, int total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Interest sweep skipped account '{accountId}'")]
    public static partial void AccountFailed(ILogger logger, string accountId, Exception exception);
}
