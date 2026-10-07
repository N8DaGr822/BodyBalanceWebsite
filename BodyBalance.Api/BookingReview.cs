using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BodyBalance.Api;

public sealed record ReviewAccess(Guid RequestId, string? Token);
public sealed record BookingDecision(Guid RequestId, string? Token, string? Status, decimal? Price, string? Location);
public sealed record BookingReviewDetails(Guid RequestId, string PractitionerName, string CustomerName,
    string CustomerEmail, string? CustomerPhone, string ServiceName, DateTime StartAtUtc, DateTime EndAtUtc,
    string TimeZoneId, decimal Price, string CurrencyCode, string Location, bool IsFirstVisitRequested, string Status);

public static class BookingReview
{
    // Approval Links: The server secret recreates the same token for notification retries; SQL stores only its hash.
    public static string CreateToken(Guid requestId, int practitionerId, string? signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new InvalidOperationException("BookingReviewSigningKey must contain at least 32 random bytes.");
        var value = $"booking-review:v1:{requestId:D}:{practitionerId.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(value)));
    }

    public static byte[]? TokenHash(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigit)
        ? SHA256.HashData(Encoding.UTF8.GetBytes(token.ToLowerInvariant())) : null;

    public static BookingDecision Validate(BookingDecision decision)
    {
        if (decision.Status is not ("Confirmed" or "Cancelled"))
            throw new BookingException("Choose approve or decline.");
        if (decision.Status == "Cancelled") return decision with { Price = null, Location = null };
        if (decision.Price is not { } price || price < 0 || price > 99999999.99m || decimal.Round(price, 2) != price)
            throw new BookingException("Enter the agreed price with no more than two decimal places.");
        var location = decision.Location?.Trim();
        if (string.IsNullOrWhiteSpace(location) || location.Length > 500
            || location.Contains("to be confirmed", StringComparison.OrdinalIgnoreCase))
            throw new BookingException("Enter the confirmed appointment address (up to 500 characters).");
        return decision with { Location = location };
    }
}
