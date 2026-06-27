namespace ActorBank.Api.Contracts;

/// <summary>Auth request/response payloads.</summary>
public record RegisterRequest(string Username, string Password);

public record TokenRequest(string Username, string Password);

public record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string AccountId);

public record JwksResponse(string Algorithm, string KeyId, string PublicKey);
