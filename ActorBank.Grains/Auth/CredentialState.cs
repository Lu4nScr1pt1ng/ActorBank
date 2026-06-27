namespace ActorBank.Grains.Auth;

/// <summary>Persisted credential: a salted password hash bound to an account id.</summary>
[GenerateSerializer]
public sealed class CredentialState
{
    [Id(0)] public bool IsRegistered { get; set; }
    [Id(1)] public byte[] Salt { get; set; } = [];
    [Id(2)] public byte[] Hash { get; set; } = [];
    [Id(3)] public int Iterations { get; set; }
    [Id(4)] public string AccountId { get; set; } = "";
}
