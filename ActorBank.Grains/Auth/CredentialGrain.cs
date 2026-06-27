namespace ActorBank.Grains.Auth;

/// <summary>
/// A virtual actor holding one user's credential (grain key = username). Single-threaded access
/// means register/authenticate never race. State is plain persistent storage — no transaction
/// needed, since a credential touches only itself.
/// </summary>
public sealed class CredentialGrain : Grain, ICredentialGrain
{
    private readonly IPersistentState<CredentialState> _state;

    public CredentialGrain(
        [PersistentState("credential", "credentialStore")] IPersistentState<CredentialState> state)
    {
        _state = state;
    }

    private string Username => this.GetPrimaryKeyString();

    public async Task Register(string password)
    {
        if (_state.State.IsRegistered)
            throw new UserAlreadyExistsException(Username);
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new InvalidCredentialsException("Password must be at least 8 characters.");

        var (salt, hash, iterations) = PasswordHasher.Hash(password);
        _state.State.Salt = salt;
        _state.State.Hash = hash;
        _state.State.Iterations = iterations;
        // The account a user controls is their own username — never client-supplied.
        _state.State.AccountId = Username;
        _state.State.IsRegistered = true;
        await _state.WriteStateAsync();
    }

    public Task<string> Authenticate(string password)
    {
        var s = _state.State;
        if (!s.IsRegistered || !PasswordHasher.Verify(password, s.Salt, s.Hash, s.Iterations))
            throw new InvalidCredentialsException("Invalid username or password.");

        return Task.FromResult(s.AccountId);
    }

    public Task<bool> Exists() => Task.FromResult(_state.State.IsRegistered);
}
