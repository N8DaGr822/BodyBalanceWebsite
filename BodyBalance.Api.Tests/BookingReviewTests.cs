using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BodyBalance.Api.Tests;

public class BookingReviewTests
{
    [Theory]
    [InlineData(1, 18)]
    [InlineData(7, 17)]
    public void AppointmentChangesUseTheAppointmentZoneRatherThanBrowserZone(int month, int utcHour)
    {
        var local = new DateTime(2027, month, 15, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTime(2027, month, 15, utcHour, 0, 0, DateTimeKind.Utc),
            BookingReview.ChangedStartUtc(new(local, 0), "America/Chicago"));
    }

    [Theory]
    [InlineData(2027, 3, 14, 2, 30)]
    [InlineData(2027, 11, 7, 1, 30)]
    [InlineData(1999, 1, 1, 12, 0)]
    [InlineData(2101, 1, 1, 12, 0)]
    public void AppointmentChangesRejectInvalidOrAmbiguousLocalTimes(int year, int month, int day, int hour, int minute)
        => Assert.Throws<BookingException>(() => BookingReview.ChangedStartUtc(
            new(new DateTime(year, month, day, hour, minute, 0), 0), "America/Chicago"));

    [Fact]
    public void AppointmentChangesRejectMissingDateNegativeRevisionAndPartialMinutes()
    {
        Assert.Throws<BookingException>(() => BookingReview.ChangedStartUtc(new(default, 0), "America/Chicago"));
        Assert.Throws<BookingException>(() => BookingReview.ChangedStartUtc(new(new(2027, 1, 15), -1), "America/Chicago"));
        Assert.Throws<BookingException>(() => BookingReview.ChangedStartUtc(new(new(2027, 1, 15, 12, 0, 1), 0), "America/Chicago"));
    }

    [Fact]
    public void UpdatedConfirmationIncludesNewTimeAndUsesADistinctKeyForEachChange()
    {
        var start = new DateTime(2027, 7, 15, 18, 0, 0, DateTimeKind.Utc);
        var details = new BookingReviewDetails(Guid.NewGuid(), "Mary", "Alex", "customer@example.com", null,
            "Reiki", start, start.AddMinutes(30), "America/Chicago", 44m, "USD", "Studio", false, "Confirmed", 1);
        var message = BookingReview.RescheduledMessage(details);
        Assert.Equal(details.CustomerEmail, message.Recipient);
        Assert.Contains("updated", message.Subject);
        Assert.Contains("updated your appointment date and time", message.EmailText);
        Assert.Contains("1:00 PM - 1:30 PM", message.EmailText);
        Assert.Contains("UTC-05:00", message.EmailText);
        Assert.Contains("44.00 USD", message.EmailText);
        Assert.Contains("Studio", message.EmailText);
        Assert.DoesNotContain("review-request", message.EmailText);
        Assert.Equal(message.IdempotencyKey, BookingReview.RescheduledMessage(details).IdempotencyKey);
        Assert.NotEqual(message.IdempotencyKey, BookingReview.RescheduledMessage(details with { ScheduleRevision = 2 }).IdempotencyKey);
    }

    [Theory]
    [InlineData("Confirmed", "Cancelled", true, true)]
    [InlineData("Confirmed", "Cancelled", false, false)]
    [InlineData("Completed", "Cancelled", true, false)]
    [InlineData("Cancelled", "Confirmed", true, false)]
    [InlineData("Confirmed", "Confirmed", true, false)]
    public void ConfirmedCancellationRequiresPractitionerAuthentication(string current, string next, bool authenticated, bool expected)
        => Assert.Equal(expected, BookingReview.CanCancelConfirmed(current, next, authenticated));

    [Fact]
    public void CancellationEmailIdentifiesTheCustomerAndAppointmentWithoutReviewCredentials()
    {
        var details = new BookingReviewDetails(Guid.NewGuid(), "Mary", "Client", "client@example.com", null,
            "Dynamic Body Balancing", new DateTime(2026, 7, 20, 18, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 20, 19, 0, 0, DateTimeKind.Utc), "America/Chicago", 77, "USD", "Private address", false, "Cancelled");
        var message = BookingReview.CancellationMessage(details);
        Assert.Equal("client@example.com", message.Recipient);
        Assert.Contains("cancelled", message.Subject);
        Assert.Contains("Mary", message.EmailText);
        Assert.Contains("Dynamic Body Balancing", message.EmailText);
        Assert.Contains("1:00 PM", message.EmailText);
        Assert.Contains("UTC-05:00", message.EmailText);
        Assert.DoesNotContain("Private address", message.EmailText);
        Assert.DoesNotContain("review-request", message.EmailText);
        Assert.Equal(message.IdempotencyKey, BookingReview.CancellationMessage(details).IdempotencyKey);
    }

    private const string Key = "test-key-with-at-least-thirty-two-bytes";

    [Fact]
    public void ReviewTokensAreBoundToTheRequestPractitionerAndSecret()
    {
        var id = Guid.NewGuid();
        var token = BookingReview.CreateToken(id, 1, Key);
        Assert.Equal(token, BookingReview.CreateToken(id, 1, Key));
        Assert.NotEqual(token, BookingReview.CreateToken(Guid.NewGuid(), 1, Key));
        Assert.NotEqual(token, BookingReview.CreateToken(id, 2, Key));
        Assert.NotEqual(token, BookingReview.CreateToken(id, 1, Key + "changed"));
        Assert.Equal(32, BookingReview.TokenHash(token)!.Length);
        Assert.Equal(BookingReview.TokenHash(token), BookingReview.TokenHash(token.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("weak-key")]
    public void UnconfiguredOrWeakSigningKeysCannotIssueLinks(string? key) =>
        Assert.Throws<InvalidOperationException>(() => BookingReview.CreateToken(Guid.NewGuid(), 1, key));

    [Fact]
    public void PublicRequestReferencesAreNotReviewCredentials()
    {
        Assert.Null(BookingReview.TokenHash(null));
        Assert.Null(BookingReview.TokenHash(Guid.NewGuid().ToString()));
        Assert.Null(BookingReview.TokenHash(new string('z', 64)));
    }

    [Theory]
    [InlineData(null, "123 Test Street")]
    [InlineData(-1d, "123 Test Street")]
    [InlineData(1.001, "123 Test Street")]
    [InlineData(100000000d, "123 Test Street")]
    [InlineData(77d, " ")]
    [InlineData(77d, "Leavenworth - exact address to be confirmed")]
    public void ApprovalRequiresAValidAgreedPriceAndConfirmedAddress(double? price, string location)
    {
        var decision = new BookingDecision(Guid.NewGuid(), "token", "Confirmed", (decimal?)price, location);
        Assert.Throws<BookingException>(() => BookingReview.Validate(decision));
    }

    [Fact]
    public void DecliningDoesNotRequireOrChangePriceAndLocation()
    {
        var decision = BookingReview.Validate(new(Guid.NewGuid(), "token", "Cancelled", -10, " "));
        Assert.Null(decision.Price);
        Assert.Null(decision.Location);
    }

    [Fact]
    public void ApprovalsTrimTheAddressAndRejectOtherStatusTransitions()
    {
        var decision = new BookingDecision(Guid.NewGuid(), "token", "Confirmed", 77, " 123 Test Street ");
        Assert.Equal("123 Test Street", BookingReview.Validate(decision).Location);
        Assert.Throws<BookingException>(() => BookingReview.Validate(decision with { Status = "Completed" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-base64")]
    [InlineData("e30=")]
    public void MissingOrMalformedPrincipalFailsClosed(string? value) => Assert.Null(PractitionerAccess.ReadIdentity(value));

    [Fact]
    public void GoogleAccountsRequireTheirOwnExplicitPractitionerAssignment()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["PractitionerAccess:google:mary123"] = "1", ["PractitionerAccess:google:leslie123"] = "2" }).Build();
        var identity = new PractitionerIdentity("google", "mary123", "mary@example.com", ["authenticated"]);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity)));
        Assert.NotNull(PractitionerAccess.ReadIdentity(encoded));
        Assert.Equal([1], PractitionerAccess.AllowedPractitioners(identity, configuration));
        Assert.Equal([2], PractitionerAccess.AllowedPractitioners(identity with { UserId = "leslie123" }, configuration));
        Assert.Empty(PractitionerAccess.AllowedPractitioners(identity with { UserId = "unassigned" }, configuration));
        Assert.Empty(PractitionerAccess.AllowedPractitioners(identity with { IdentityProvider = "github" }, configuration));
    }

    [Fact]
    public void AccountAccessUsesTheVerifiedProviderAndUserIdNotTheDisplayEmail()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["PractitionerAccess:github:abc123"] = "1, 2, 2, invalid, -1" }).Build();
        var identity = new PractitionerIdentity("github", "abc123", "client@example.com", ["authenticated"]);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity)));
        Assert.Equal(identity.UserId, PractitionerAccess.ReadIdentity(encoded)!.UserId);
        Assert.Equal([1, 2], PractitionerAccess.AllowedPractitioners(identity, configuration));
        Assert.Empty(PractitionerAccess.AllowedPractitioners(identity with { UserId = "other" }, configuration));
        Assert.Empty(PractitionerAccess.AllowedPractitioners(identity with { IdentityProvider = "aad" }, configuration));
        var anonymous = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity with { UserRoles = ["anonymous"] })));
        Assert.Null(PractitionerAccess.ReadIdentity(anonymous));
    }
}
