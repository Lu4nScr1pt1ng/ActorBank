using ActorBank.Api.Auth;
using ActorBank.Api.Contracts;

namespace ActorBank.Api.Endpoints;

/// <summary>
/// The <c>/auth</c> routes: register a credential, exchange it for a post-quantum signed token,
/// and publish the public key used to verify tokens. These are anonymous (no token required).
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", async (RegisterRequest request, IGrainFactory grains) =>
        {
            await grains.GetGrain<ICredentialGrain>(request.Username).Register(request.Password);
            // The account id a user controls is their own username.
            return TypedResults.Created($"/auth/users/{request.Username}",
                new { request.Username, AccountId = request.Username });
        })
        .WithSummary("Register a username/password (you control the account named after your username)");

        auth.MapPost("/token", async (TokenRequest request, IGrainFactory grains, PqcTokenService tokens) =>
        {
            var accountId = await grains.GetGrain<ICredentialGrain>(request.Username).Authenticate(request.Password);
            var token = tokens.IssueToken(accountId, request.Username);
            return TypedResults.Ok(new TokenResponse(token, "Bearer", tokens.LifetimeSeconds, accountId));
        })
        .WithSummary("Exchange credentials for a post-quantum (ML-DSA-65) signed token");

        auth.MapGet("/jwks", (PqcTokenService tokens) =>
            TypedResults.Ok(new JwksResponse(tokens.Algorithm, tokens.KeyId, tokens.PublicKeyBase64())))
        .WithSummary("Public key and algorithm used to verify tokens");

        return app;
    }
}
