using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace BodyBalance.Api;

public sealed record PractitionerLoginRequest(string? Email, string? Code = null, bool RememberMe = false);
public sealed record PractitionerSession(int PractitionerId, string Email);

public static class PractitionerLogin
{
    public const string CookieName = "__Host-BBPractitioner";
    public const int SessionHours = 8;
    public static int SessionHoursFor(bool rememberMe) => rememberMe ? 30 * 24 : SessionHours;

    public static string NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value.Length > 254 || !new EmailAddressAttribute().IsValid(value))
            throw new BookingException("Enter a valid email address.");
        return value;
    }

    public static bool ValidCode(string? code) => code is { Length: 6 } && code.All(char.IsAsciiDigit);
    public static string CreateCode() => RandomNumberGenerator.GetInt32(1000000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    public static string CreateSession() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool ValidSession(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    public static byte[] SessionHash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    // Code Protection: A server-held key prevents offline guessing of six-digit codes from a database copy.
    public static byte[] CodeHash(string email, string code, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("The sign-in signing key is not configured.");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"practitioner-login\n{email}\n{code}"));
    }

    public static string? ReadSession(string? cookies)
    {
        var values = (cookies ?? "").Split(';', StringSplitOptions.TrimEntries)
            .Where(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal)).ToArray();
        var value = values.Length == 1 ? values[0][(CookieName.Length + 1)..] : null;
        return ValidSession(value) ? value : null;
    }

    public static string SessionCookie(string token, bool clear = false, bool rememberMe = false) =>
        $"{CookieName}={token}; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age={(clear ? 0 : SessionHoursFor(rememberMe) * 3600)}";
}
