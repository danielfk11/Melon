using System.Security.Cryptography;
using System.Text;

namespace MelonMQ.Broker.Core;

public static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int DefaultIterations = 100_000;

    public static string HashPassword(string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        }

        if (iterations < 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "Iterations must be at least 10000.");
        }

        Span<byte> salt = stackalloc byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(salt);

        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string providedPassword, string storedCredential)
    {
        if (string.IsNullOrEmpty(providedPassword) || string.IsNullOrEmpty(storedCredential))
        {
            return false;
        }

        if (TryParseHashedCredential(storedCredential, out var iterations, out var saltBytes, out var expectedHash))
        {
            var providedHash = Rfc2898DeriveBytes.Pbkdf2(providedPassword, saltBytes, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedPassword);
        var expectedBytes = Encoding.UTF8.GetBytes(storedCredential);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    public static bool IsHashedCredential(string credential)
    {
        return TryParseHashedCredential(credential, out _, out _, out _);
    }

    private static bool TryParseHashedCredential(string credential, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();

        var parts = credential.Split('$');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!string.Equals(parts[0], Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out iterations) || iterations < 10_000)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= SaltSizeBytes && hash.Length >= 16;
    }
}