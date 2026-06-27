namespace ActorBank.Abstractions.Exceptions;

/// <summary>Thrown when an amount is zero or negative where a positive value is required.</summary>
[GenerateSerializer]
public sealed class InvalidAmountException : BankException
{
    [Id(0)] public decimal Amount { get; }

    public InvalidAmountException(decimal amount)
        : base($"Amount must be positive, but was {amount:C}.")
    {
        Amount = amount;
    }
}
