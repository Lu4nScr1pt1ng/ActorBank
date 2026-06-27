namespace ActorBank.Abstractions.Exceptions;

/// <summary>Thrown when a withdrawal or transfer exceeds the available balance.</summary>
[GenerateSerializer]
public sealed class InsufficientFundsException : BankException
{
    [Id(0)] public decimal Requested { get; }
    [Id(1)] public decimal Available { get; }

    public InsufficientFundsException(decimal requested, decimal available)
        : base($"Insufficient funds: requested {requested:C}, available {available:C}.")
    {
        Requested = requested;
        Available = available;
    }
}
