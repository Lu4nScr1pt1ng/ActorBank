namespace ActorBank.Abstractions.Exceptions;

/// <summary>Thrown when registering a username that is already taken.</summary>
[GenerateSerializer]
public sealed class UserAlreadyExistsException : BankException
{
    public UserAlreadyExistsException(string username)
        : base($"User '{username}' already exists.") { }
}
