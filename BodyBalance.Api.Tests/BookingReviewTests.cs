using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BodyBalance.Api.Tests;

public class BookingReviewTests
{
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
