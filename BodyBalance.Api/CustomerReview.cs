namespace BodyBalance.Api;

public sealed record CustomerReviewRequest(Guid RequestId, string? Name, string? Message, int PractitionerId);
public sealed record CustomerReviewDetails(Guid RequestId, string Name, string Message, string PractitionerName);

public static class CustomerReview
{
    public static CustomerReviewRequest Validate(CustomerReviewRequest request)
    {
        var name = request.Name?.Trim();
        var message = request.Message?.Trim();
        if (request.RequestId == Guid.Empty) throw new BookingException("Refresh the page before submitting your review.");
        if (request.PractitionerId <= 0) throw new BookingException("Choose your practitioner.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150)
            throw new BookingException("Enter a display name (up to 150 characters).");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 2000)
            throw new BookingException("Enter your review (up to 2,000 characters).");
        return request with { Name = name, Message = message };
    }
}
