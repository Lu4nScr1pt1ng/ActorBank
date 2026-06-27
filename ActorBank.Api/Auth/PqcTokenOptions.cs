namespace ActorBank.Api.Auth;

/// <summary>Configuration for the post-quantum token service (bound from the "Pqc" section).</summary>
public sealed class PqcTokenOptions
{
    public string Issuer { get; set; } = "actorbank";
    public string Audience { get; set; } = "actorbank-api";

    /// <summary>Path to the ML-DSA private key (PKCS#8). Generated on first run if missing.</summary>
    public string KeyFilePath { get; set; } = "pqc-signing-key.pkcs8";

    public int TokenLifetimeMinutes { get; set; } = 60;
}
