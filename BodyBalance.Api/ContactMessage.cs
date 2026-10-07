using System.ComponentModel.DataAnnotations;

namespace BodyBalance.Api;

public sealed record ContactRequest(Guid RequestId, string? Name, string? Email, string? Phone, string? Message);
public sealed record ContactReceipt(Guid RequestId);
public sealed record ContactNotification(Guid RequestId, string Channel, Guid ClaimId, int Attempts, ContactRequest Contact);

public static class ContactMessage
{
    public static ContactRequest Validate(ContactRequest request)
    {
        var name = request.Name?.Trim();
        var email = request.Email?.Trim().ToLowerInvariant();
        var phone = request.Phone?.Trim();
        var message = request.Message?.Trim();
        if (request.RequestId == Guid.Empty) throw new BookingException("Refresh the page before sending your message.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150) throw new BookingException("Enter your name (up to 150 characters).");
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !new EmailAddressAttribute().IsValid(email))
            throw new BookingException("Enter a valid email address (up to 254 characters).");
        if (phone?.Length > 30) throw new BookingException("Enter a phone number of up to 30 characters.");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 4000) throw new BookingException("Enter your message (up to 4,000 characters).");
        return request with { Name = name, Email = email, Phone = string.IsNullOrEmpty(phone) ? null : phone, Message = message };
    }

    public static NotificationMessage Notification(ContactNotification notification, string? recipient) => new(
        notification.Channel, recipient, "New contact message for Return to Self Reiki",
        $"Name: {notification.Contact.Name}\nEmail: {notification.Contact.Email}\nPhone: {notification.Contact.Phone ?? "Not provided"}\nReference: {notification.RequestId:D}\n\n{notification.Contact.Message}",
        $"Return to Self Reiki: New contact message received (ref {notification.RequestId.ToString("N")[..8]}). Check email for details. Reply STOP to unsubscribe.",
        notification.Contact.Email);
}
