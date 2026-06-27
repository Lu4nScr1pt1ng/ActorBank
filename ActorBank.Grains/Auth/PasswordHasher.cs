using System.Security.Cryptography;

namespace ActorBank.Grains.Auth;

/// <summary>
/// Salted PBKDF2-SHA256 password hashing. Symmetric/hash-based KDFs remain safe against quantum
/// attackers given adequate parameters (Grover only halves the effective strength), so the
/// quantum-resistant concern is the token signature, not this.
/// </summary>
internal static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;

    public static (byte[] Salt, byte[] Hash, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return (salt, hash, Iterations);
    }

    public static bool Verify(string password, byte[] salt, byte[] expectedHash, int iterations)
    {
        if (salt.Length == 0 || expectedHash.Length == 0)
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }
}
