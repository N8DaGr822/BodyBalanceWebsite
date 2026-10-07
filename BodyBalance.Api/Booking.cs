using System.ComponentModel.DataAnnotations;

namespace BodyBalance.Api;

public sealed record BookingRequest(Guid RequestId, int PractitionerId, int ServiceId,
    DateTimeOffset StartAtUtc, string? CustomerName, string? CustomerEmail, string? CustomerPhone,
    bool IsFirstVisit);

public sealed record BookingReceipt(Guid RequestId, string Status, DateTime StartAtUtc,
    DateTime EndAtUtc, string ServiceName, string TimeZoneId, string Location);

public sealed class BookingException(string message, bool conflict = false) : Exception(message)
{
    public bool Conflict { get; } = conflict;
}

public static class Booking
{
    public static BookingRequest Validate(BookingRequest request)
    {
        var name = request.CustomerName?.Trim();
        var email = request.CustomerEmail?.Trim().ToLowerInvariant();
        var phone = request.CustomerPhone?.Trim();
        if (request.RequestId == Guid.Empty || request.PractitionerId <= 0 || request.ServiceId <= 0
            || request.StartAtUtc.Year < 2000 || request.StartAtUtc.Year > 2100)
            throw new BookingException("Choose a valid practitioner, service, and appointment time.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150)
            throw new BookingException("Enter your name (up to 150 characters).");
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !new EmailAddressAttribute().IsValid(email))
            throw new BookingException("Enter a valid email address (up to 254 characters).");
        if (phone?.Length > 30)
            throw new BookingException("Enter a phone number of up to 30 characters.");
        return request with { CustomerName = name, CustomerEmail = email,
            CustomerPhone = string.IsNullOrEmpty(phone) ? null : phone };
    }

    // First Visit: A claim alone cannot override an existing request or visit with this practitioner.
    public static decimal Price(Service service, bool isFirstVisit, bool hasHistory) =>
        isFirstVisit && !hasHistory ? service.FirstTimeClientPrice ?? service.Price : service.Price;
}
