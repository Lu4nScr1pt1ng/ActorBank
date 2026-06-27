using ActorBank.Api.Auth;
using ActorBank.Api.Endpoints;
using ActorBank.Api.Infrastructure;
using ActorBank.Api.Serialization;
using Microsoft.AspNetCore.Authentication;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Co-host the Orleans silo (clustering + ADO.NET storage + transactions).
builder.AddActorBankSilo();
builder.Services.Configure<InterestOptions>(builder.Configuration.GetSection("Interest"));

// Post-quantum authentication: ML-DSA-65 signed bearer tokens.
builder.Services.Configure<PqcTokenOptions>(builder.Configuration.GetSection("Pqc"));
// Short-lived cache of verified tokens so hot paths skip re-running the lattice signature check.
// SizeLimit (entries) bounds memory; only valid tokens are ever cached.
builder.Services.AddMemoryCache(options => options.SizeLimit = 50_000);
builder.Services.AddSingleton<PqcTokenService>();
builder.Services
    .AddAuthentication(PqcBearerDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, PqcBearerHandler>(PqcBearerDefaults.Scheme, null);
builder.Services.AddAuthorization();

// Translate domain exceptions into RFC 7807 problem responses.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BankExceptionHandler>();

// Use source-generated JSON metadata for the API payloads (no per-request reflection).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonSerializerContext.Default));

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

// API docs: OpenAPI document + interactive Scalar UI at /scalar/v1.
app.MapOpenApi();
app.MapScalarApiReference();

app.MapAuthEndpoints();
app.MapAccountEndpoints();

app.MapGet("/", () => Results.Ok(new { service = "ActorBank", status = "ok", docs = "/scalar/v1" }));

app.Run();
