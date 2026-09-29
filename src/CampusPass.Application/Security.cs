using System.Security.Cryptography;
using System.Text;

namespace CampusPass.Application;

/// <summary>PBKDF2-HMAC-SHA512, 210,000 iterations (OWASP guidance), 16-byte salt, constant-time comparison.</summary>
public static class PasswordHasher
{
    public const int Iterations = 210_000;
    private const int KeySize = 32;

    public static (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, KeySize);
        return (Convert.ToBase64String(key), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string hash, string salt)
    {
        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), Iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>A one-time password that is easy to read aloud or type from an SMS: no 0/O, 1/l/I.</summary>
    public static string NewTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        Span<char> c = stackalloc char[10];
        for (var i = 0; i < c.Length; i++) c[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"{new string(c[..5])}-{new string(c[5..])}";
    }
}

/// <summary>
/// Gatepass QR token = "{random id}.{HMAC-SHA256(key, id)}", base64url. The QR carries no personal data;
/// the gate looks the pass up on the server, so an edited screenshot or PDF can never verify.
/// </summary>
public class GatepassTokens(string key)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(key.Length >= 32 ? key : throw new ArgumentException("Gatepass key must be at least 32 characters."));

    public static string NewId() => B64(RandomNumberGenerator.GetBytes(12));

    public string ToToken(string id) => $"{id}.{B64(Sign(id))}";

    public bool TryParse(string? token, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        byte[] given;
        try { given = FromB64(parts[1]); } catch (FormatException) { return false; }
        if (B64(given) != parts[1]) return false;                  // only the canonical encoding is accepted
        if (!CryptographicOperations.FixedTimeEquals(given, Sign(parts[0]))) return false;
        id = parts[0];
        return true;
    }

    private byte[] Sign(string id) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(id));

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
