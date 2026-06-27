namespace ActorBank.Api.Contracts;

/// <summary>Lightweight response returned by balance-changing endpoints.</summary>
public record BalanceResponse(string AccountId, decimal Balance);
