using Microsoft.Extensions.Logging;

namespace ActorBank.Grains.Accounts;

/// <summary>Source-generated, allocation-free log messages for <see cref="AccountGrain"/>.</summary>
internal static partial class AccountGrainLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Opened '{accountId}' for {owner}")]
    public static partial void AccountOpened(ILogger logger, string accountId, string owner);
}
