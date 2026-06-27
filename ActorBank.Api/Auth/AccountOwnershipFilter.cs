using System.Security.Claims;

namespace ActorBank.Api.Auth;

/// <summary>
/// Endpoint filter for <c>/accounts/{id}/...</c>: ensures the authenticated token's subject
/// matches the account in the route, so a user can only act on their own account.
/// </summary>
public sealed class AccountOwnershipFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var routeId = http.Request.RouteValues["id"]?.ToString();
        var subject = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (routeId is null || subject is null || !string.Equals(routeId, subject, StringComparison.Ordinal))
        {
            return Results.Problem(
                title: "Forbidden",
                detail: "Your token does not grant access to this account.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
