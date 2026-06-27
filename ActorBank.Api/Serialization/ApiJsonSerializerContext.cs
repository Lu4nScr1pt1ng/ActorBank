using System.Text.Json.Serialization;
using ActorBank.Api.Contracts;

namespace ActorBank.Api.Serialization;

/// <summary>
/// Source-generated JSON metadata for the API payloads. Removes per-request reflection,
/// trims startup work and is trim/AOT-friendly. Enums serialize as strings for readable output.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AccountStatement))]
[JsonSerializable(typeof(TransactionRecord))]
[JsonSerializable(typeof(BalanceResponse))]
[JsonSerializable(typeof(OpenRequest))]
[JsonSerializable(typeof(AmountRequest))]
[JsonSerializable(typeof(TransferRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(JwksResponse))]
public partial class ApiJsonSerializerContext : JsonSerializerContext;
