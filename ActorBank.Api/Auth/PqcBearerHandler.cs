using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ActorBank.Api.Auth;

/// <summary>
/// Authentication handler for the <c>PqcBearer</c> scheme: reads the <c>Authorization: Bearer</c>
/// header and validates the post-quantum (ML-DSA-65) token via <see cref="PqcTokenService"/>.
/// </summary>
public sealed class PqcBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string Prefix = "Bearer ";
    private readonly PqcTokenService _tokens;

    public PqcBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PqcTokenService tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var header = values.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = header[Prefix.Length..].Trim();
        try
        {
            var principal = _tokens.ValidateToken(token);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex)
        {
            return Task.FromResult(AuthenticateResult.Fail(ex.Message));
        }
    }
}
