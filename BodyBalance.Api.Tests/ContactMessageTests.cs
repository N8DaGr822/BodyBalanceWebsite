using Xunit;

namespace BodyBalance.Api.Tests;

public class ContactMessageTests
{
    private static ContactRequest Request() => new(Guid.NewGuid(), "Test Client", "client@example.com", null, "I'd like more information.", 1);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequiresASelectedPractitioner(int? practitionerId) =>
        Assert.Throws<BookingException>(() => ContactMessage.Validate(Request() with { PractitionerId = practitionerId }));

    [Theory]
    [InlineData(1, "mary@example.com")]
    [InlineData(2, "leslie@example.com")]
    public void SelectedPractitionerReceivesTheMessageWhenSharedEmailIsNotConfigured(int practitionerId, string email)
    {
        var contact = Request() with { PractitionerId = practitionerId };
        var notification = new ContactNotification(contact.RequestId, "Email", Guid.NewGuid(), 1, contact, email);
        Assert.Equal(email, ContactMessage.Recipient(notification, null));
        Assert.Equal(email, ContactMessage.Recipient(notification, " "));
        Assert.Null(ContactMessage.Recipient(notification with { PractitionerEmail = null }, null));
        Assert.Null(ContactMessage.Recipient(notification with { Channel = "Sms" }, "+15551234567"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConfiguredSharedEmailOverridesPractitionerEmailButNotSms(int practitionerId)
    {
        var contact = Request() with { PractitionerId = practitionerId };
        var notification = new ContactNotification(contact.RequestId, "Email", Guid.NewGuid(), 1, contact,
            "personal@example.com", "+15551234567", "Selected practitioner");
        Assert.Equal("hello@entwinedandunwind.com", ContactMessage.Recipient(notification, "hello@entwinedandunwind.com"));
        Assert.Equal("+15551234567", ContactMessage.Recipient(notification with { Channel = "Sms" }, "+15559999999"));
        var message = ContactMessage.Notification(notification, "hello@entwinedandunwind.com");
        Assert.Contains("For: Selected practitioner", message.EmailText);
        Assert.Equal(contact.Email, message.ReplyTo);
    }

    [Fact]
    public void LegacyMessagesRetainTheirSharedDestination()
    {
        var contact = Request() with { PractitionerId = null };
        var notification = new ContactNotification(contact.RequestId, "Email", Guid.NewGuid(), 1, contact);
        Assert.Equal("shared@example.com", ContactMessage.Recipient(notification, "shared@example.com"));
    }

    [Fact]
    public void NormalizesContactDetailsWithoutChangingTheRetryReference()
    {
        var request = Request() with { Name = " Test Client ", Email = " CLIENT@Example.COM ", Phone = "  ", Message = " Hello! \n" };
        var result = ContactMessage.Validate(request);
        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal("Test Client", result.Name);
        Assert.Equal("client@example.com", result.Email);
        Assert.Null(result.Phone);
        Assert.Equal("Hello!", result.Message);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("message")]
    public void RejectsMissingRequiredValues(string field)
    {
        var request = Request();
        request = field switch
        {
            "id" => request with { RequestId = Guid.Empty },
            "name" => request with { Name = " " },
            "email" => request with { Email = null },
            _ => request with { Message = "\n " }
        };
        Assert.Throws<BookingException>(() => ContactMessage.Validate(request));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("message")]
    public void RejectsValuesThatExceedDatabaseLimits(string field)
    {
        var request = Request();
        request = field switch
        {
            "name" => request with { Name = new string('a', 151) },
            "email" => request with { Email = new string('a', 243) + "@example.com" },
            "phone" => request with { Phone = new string('1', 31) },
            _ => request with { Message = new string('a', 4001) }
        };
        Assert.Throws<BookingException>(() => ContactMessage.Validate(request));
    }

    [Fact]
    public void AcceptsMaximumLengthMessageAndRejectsMalformedEmail()
    {
        var request = Request() with { Message = new string('a', 4000) };
        Assert.Equal(4000, ContactMessage.Validate(request).Message!.Length);
        Assert.Throws<BookingException>(() => ContactMessage.Validate(request with { Email = "not-an-email" }));
    }

    [Fact]
    public void ContactEmailHasReplyDetailsButSmsOnlyAlertsTheSharedContact()
    {
        var request = Request();
        var notification = ContactMessage.Notification(new(request.RequestId, "Sms", Guid.NewGuid(), 1, request), "+15551234567");
        Assert.Equal("+15551234567", notification.Recipient);
        Assert.Equal(request.Email, notification.ReplyTo);
        Assert.Contains(request.Name!, notification.EmailText);
        Assert.Contains(request.Email!, notification.EmailText);
        Assert.Contains(request.Message!, notification.EmailText);
        Assert.Contains(request.RequestId.ToString("N")[..8], notification.SmsText);
        Assert.DoesNotContain(request.Name!, notification.SmsText);
        Assert.DoesNotContain(request.Email!, notification.SmsText);
        Assert.DoesNotContain(request.Message!, notification.SmsText);
        Assert.DoesNotContain("review-request", notification.EmailText);
    }
}
