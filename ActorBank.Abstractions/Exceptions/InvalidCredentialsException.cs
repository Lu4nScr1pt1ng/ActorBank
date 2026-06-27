namespace ActorBank.Abstractions.Exceptions;

/// <summary>Thrown when registration or login credentials are rejected.</summary>
[GenerateSerializer]
public sealed class InvalidCredentialsException : BankException
{
    public InvalidCredentialsException(string message) : base(message) { }
}
