namespace ActorBank.Api.Contracts;

/// <summary>Request bodies for the account endpoints.</summary>
public record OpenRequest(string Owner, decimal OpeningDeposit = 0m);

public record AmountRequest(decimal Amount, string? Description = null);

public record TransferRequest(string ToAccountId, decimal Amount, string? Description = null);
