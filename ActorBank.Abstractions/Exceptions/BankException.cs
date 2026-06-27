namespace ActorBank.Abstractions.Exceptions;

/// <summary>Base type for all expected, domain-level banking errors.</summary>
[GenerateSerializer]
public abstract class BankException : Exception
{
    protected BankException(string message) : base(message) { }
}
