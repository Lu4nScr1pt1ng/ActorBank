namespace ActorBank.Abstractions.Auth;

/// <summary>
/// Stores a single user's login credential (grain key = username). The account a user may operate
/// is their own username — it is never client-supplied, so nobody can register a credential that
/// claims someone else's account. The password is never stored in clear — only a salted hash.
/// </summary>
public interface ICredentialGrain : IGrainWithStringKey
{
    /// <summary>Registers the credential. Fails if the username already exists.</summary>
    Task Register(string password);

    /// <summary>Verifies the password and returns the bound account id (the username), or throws.</summary>
    /// <exception cref="InvalidCredentialsException">When the password does not match.</exception>
    Task<string> Authenticate(string password);

    /// <summary>Whether this username has been registered.</summary>
    Task<bool> Exists();
}
