using System.Buffers.Text;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ActorBank.Api.Auth;

/// <summary>
/// Issues and validates compact JWS tokens signed with <b>ML-DSA-65</b> (NIST FIPS 204), a
/// post-quantum signature scheme. Tokens look like a JWT (<c>header.payload.signature</c>) but the
/// signature is quantum-resistant instead of RSA/ECDSA.
///
/// <b>Signing</b> (rare — only at <c>/auth/token</c>) runs under a lock on the private key.
/// <b>Verification</b> (every authenticated request) is lock-free: each thread gets its own
/// public-key verifier, so token checks run fully in parallel. A successful verification is cached
/// for a short window keyed by the exact token, so repeated requests with the same token skip the
/// (relatively expensive) lattice signature check — without weakening security: a tampered token is
/// a different key (cache miss → full verify → reject), and expiry is still enforced on every hit.
/// </summary>
public sealed class PqcTokenService : IDisposable
{
    private const string AlgorithmId = "ML-DSA-65";

    /// <summary>How long a validated token's principal may be served from cache (never past its exp).</summary>
    private const int VerifyCacheSeconds = 30;

    private readonly PqcTokenOptions _options;
    private readonly MLDsa _key;                 // private key — signing only, guarded by _gate
    private readonly byte[] _publicKey;          // SubjectPublicKeyInfo, immutable after construction
    private readonly ThreadLocal<MLDsa> _verifiers; // one verifier per thread → concurrent verification
    private readonly IMemoryCache _validated;    // verified token → principal (short TTL)
    private readonly string _kid;
    private readonly Lock _gate = new();

    private sealed record CachedToken(ClaimsPrincipal Principal, long ExpiresAtUnix);

    public PqcTokenService(IOptions<PqcTokenOptions> options, ILogger<PqcTokenService> logger, IMemoryCache validated)
    {
        _options = options.Value;
        _validated = validated;
        _key = LoadOrCreateKey(_options.KeyFilePath, logger);
        _publicKey = _key.ExportSubjectPublicKeyInfo();
        _verifiers = new ThreadLocal<MLDsa>(
            () => MLDsa.ImportSubjectPublicKeyInfo(_publicKey), trackAllValues: true);
        _kid = ComputeKeyId(_publicKey);
    }

    public string Algorithm => AlgorithmId;
    public string KeyId => _kid;
    public int LifetimeSeconds => _options.TokenLifetimeMinutes * 60;

    /// <summary>Signs a token for the given subject (account id).</summary>
    public string IssueToken(string subject, string username)
    {
        var now = DateTimeOffset.UtcNow;
        var header = new Dictionary<string, object>
        {
            ["alg"] = AlgorithmId,
            ["typ"] = "JWT",
            ["kid"] = _kid,
        };
        var payload = new Dictionary<string, object>
        {
            ["iss"] = _options.Issuer,
            ["aud"] = _options.Audience,
            ["sub"] = subject,
            ["name"] = username,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(_options.TokenLifetimeMinutes).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        var headerSegment = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadSegment = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = Encoding.ASCII.GetBytes($"{headerSegment}.{payloadSegment}");

        byte[] signature;
        lock (_gate)
            signature = _key.SignData(signingInput);

        return $"{headerSegment}.{payloadSegment}.{Base64Url.EncodeToString(signature)}";
    }

    /// <summary>Verifies a token's signature and claims, returning a principal on success.</summary>
    /// <exception cref="AuthenticationException">When the token is malformed, unsigned or expired.</exception>
    public ClaimsPrincipal ValidateToken(string token)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Fast path: this exact token already passed the full signature + claims check recently. The
        // signature is implicitly re-verified by the cache key (a tampered token won't match); we only
        // need to re-check time-based expiry, which is cheap and has no crypto.
        if (_validated.TryGetValue(token, out CachedToken? hit) && hit is not null)
        {
            if (hit.ExpiresAtUnix <= now)
                throw new AuthenticationException("Token has expired.");
            return hit.Principal;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new AuthenticationException("Malformed token.");

        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        byte[] signature = SafeDecode(parts[2]);

        // Lock-free: this thread's own public-key verifier.
        if (!_verifiers.Value!.VerifyData(signingInput, signature))
            throw new AuthenticationException("Invalid token signature.");

        using var doc = JsonDocument.Parse(SafeDecode(parts[1]));
        var claims = doc.RootElement;

        if (claims.TryGetProperty("exp", out var exp) && exp.GetInt64() < now)
            throw new AuthenticationException("Token has expired.");
        if (claims.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now)
            throw new AuthenticationException("Token is not yet valid.");
        if (!claims.TryGetProperty("iss", out var iss) || iss.GetString() != _options.Issuer)
            throw new AuthenticationException("Unexpected token issuer.");
        if (!claims.TryGetProperty("aud", out var aud) || aud.GetString() != _options.Audience)
            throw new AuthenticationException("Unexpected token audience.");

        var subject = claims.GetProperty("sub").GetString()
            ?? throw new AuthenticationException("Token has no subject.");
        var name = claims.TryGetProperty("name", out var n) ? n.GetString() ?? subject : subject;

        var identity = new ClaimsIdentity(authenticationType: PqcBearerDefaults.Scheme,
            nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
        identity.AddClaim(new Claim("sub", subject));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        var principal = new ClaimsPrincipal(identity);

        // Cache this verified principal until min(now + TTL, token exp), so repeated calls with the
        // same token skip the lattice verify. Only valid tokens ever reach here, so the cache can't be
        // flooded with garbage; a SizeLimit (configured in Program.cs) bounds it regardless.
        var expiresAtUnix = exp.ValueKind == JsonValueKind.Number ? exp.GetInt64() : now + VerifyCacheSeconds;
        var ttl = Math.Min(VerifyCacheSeconds, expiresAtUnix - now);
        if (ttl > 0)
            _validated.Set(token, new CachedToken(principal, expiresAtUnix), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl),
                Size = 1,
            });

        return principal;
    }

    /// <summary>The Base64-encoded SubjectPublicKeyInfo, for external token verification.</summary>
    public string PublicKeyBase64()
    {
        byte[] spki;
        lock (_gate)
            spki = _key.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(spki);
    }

    private static byte[] SafeDecode(string segment)
    {
        try
        {
            return Base64Url.DecodeFromChars(segment);
        }
        catch (FormatException)
        {
            throw new AuthenticationException("Malformed token encoding.");
        }
    }

    private static MLDsa LoadOrCreateKey(string path, ILogger logger)
    {
        if (!MLDsa.IsSupported)
            throw new PlatformNotSupportedException(
                "ML-DSA is not available on this platform (needs OpenSSL 3.5+ / a PQC-capable provider).");

        // Multiple silos may share the key file (e.g. scaled replicas booting together). The first
        // to win an exclusive create generates the key; the others fall back to reading it. This
        // keeps every node on the *same* signing key, so a token works on any node.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path))
            {
                try
                {
                    var loaded = MLDsa.ImportPkcs8PrivateKey(File.ReadAllBytes(path));
                    PqcTokenLog.KeyLoaded(logger, path);
                    return loaded;
                }
                catch (Exception) // a peer may be mid-write; wait and retry
                {
                    Thread.Sleep(100);
                    continue;
                }
            }

            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
                stream.Write(key.ExportPkcs8PrivateKey());
                stream.Flush();
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                PqcTokenLog.KeyGenerated(logger, path);
                return key;
            }
            catch (IOException) // lost the create race; loop back and read what the winner wrote
            {
                Thread.Sleep(100);
            }
        }

        throw new InvalidOperationException($"Could not load or create the signing key at '{path}'.");
    }

    private static string ComputeKeyId(byte[] subjectPublicKeyInfo)
    {
        var thumbprint = SHA256.HashData(subjectPublicKeyInfo);
        return Base64Url.EncodeToString(thumbprint.AsSpan(0, 8));
    }

    public void Dispose()
    {
        // Each thread that verified a token holds its own MLDsa verifier; dispose them all.
        if (_verifiers.Values is { } verifiers)
            foreach (var verifier in verifiers)
                verifier.Dispose();
        _verifiers.Dispose();
        _key.Dispose();
    }
}

/// <summary>Source-generated log messages for <see cref="PqcTokenService"/>.</summary>
internal static partial class PqcTokenLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded ML-DSA-65 signing key from {path}")]
    public static partial void KeyLoaded(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generated a new ML-DSA-65 signing key at {path}")]
    public static partial void KeyGenerated(ILogger logger, string path);
}
