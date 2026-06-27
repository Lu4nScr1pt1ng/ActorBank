namespace ActorBank.Abstractions.Exceptions;

/// <summary>Thrown when an operation targets an account that was never opened.</summary>
[GenerateSerializer]
public sealed class AccountNotOpenException : BankException
{
    public AccountNotOpenException(string accountId)
        : base($"Account '{accountId}' is not open.") { }
}
