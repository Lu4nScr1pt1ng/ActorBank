using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace ActorBank.Api.Infrastructure;

/// <summary>
/// Teaches the OpenAPI document about the post-quantum bearer token. A single document transformer
/// declares the <c>Bearer</c> security scheme and marks every operation whose endpoint requires
/// authorization with it — so Scalar shows an <b>Authorize</b> button and a lock on the protected
/// <c>/accounts</c> routes, and you can call them with a token. The anonymous <c>/auth</c> routes are
/// left open. (Doing both in one document transformer means the scheme reference resolves against the
/// fully-built document, so the requirement serializes correctly.)
/// </summary>
public static class OpenApiSecurity
{
    private const string SchemeId = "Bearer";

    public static IServiceCollection AddActorBankOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "ML-DSA-65 JWS",
                    In = ParameterLocation.Header,
                    Description = "Paste the accessToken returned by POST /auth/token.",
                };

                var requirement = new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeId, document)] = new List<string>(),
                };

                // Apply the requirement to operations whose endpoint asks for authorization.
                foreach (var group in context.DescriptionGroups)
                {
                    foreach (var api in group.Items)
                    {
                        var metadata = api.ActionDescriptor.EndpointMetadata;
                        var requiresAuth = metadata.OfType<IAuthorizeData>().Any()
                                           && !metadata.OfType<IAllowAnonymous>().Any();
                        if (!requiresAuth || api.RelativePath is null)
                            continue;

                        var path = "/" + api.RelativePath.TrimStart('/');
                        if (document.Paths.TryGetValue(path, out var item) && item?.Operations is { } operations)
                        {
                            foreach (var operation in operations.Values)
                                operation.Security = [requirement];
                        }
                    }
                }

                return Task.CompletedTask;
            });
        });
}
