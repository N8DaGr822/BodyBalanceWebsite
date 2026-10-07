using Xunit;

namespace BodyBalance.Api.Tests;

public class BookingTests
{
    [Theory]
    [InlineData(true, false, 77)]
    [InlineData(true, true, 111)]
    [InlineData(false, false, 111)]
    [InlineData(false, true, 111)]
    public void FirstVisitPriceRequiresAClaimAndNoHistory(bool firstVisit, bool hasHistory, decimal expected)
    {
        var service = new Service(1, "60-Minute Reiki", 60, 111, "USD", null, null, 77);
        Assert.Equal(expected, Booking.Price(service, firstVisit, hasHistory));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ThirtyMinutePromotionAppliesToAllClients(bool firstVisit, bool hasHistory)
    {
        var service = new Service(1, "30-Minute Reiki", 30, 44, "USD", null, 55, null);
        Assert.Equal(44, Booking.Price(service, firstVisit, hasHistory));
    }

    [Fact]
    public void ContactDetailsAreNormalizedBeforeHistoryAndRetryChecks()
    {
        var request = Booking.Validate(ValidRequest() with { CustomerName = "  Client  ", CustomerEmail = " CLIENT@EXAMPLE.COM ", CustomerPhone = "  " });
        Assert.Equal("Client", request.CustomerName);
        Assert.Equal("client@example.com", request.CustomerEmail);
        Assert.Null(request.CustomerPhone);
    }

    [Theory]
    [InlineData(null, "client@example.com")]
    [InlineData("   ", "client@example.com")]
    [InlineData("Client", null)]
    [InlineData("Client", "invalid")]
    public void MissingOrInvalidContactDetailsAreRejected(string? name, string? email)
    {
        Assert.Throws<BookingException>(() => Booking.Validate(ValidRequest() with { CustomerName = name, CustomerEmail = email }));
    }

    [Fact]
    public void OversizedAndMissingBookingFieldsAreRejected()
    {
        Assert.Throws<BookingException>(() => Booking.Validate(ValidRequest() with { RequestId = Guid.Empty }));
        Assert.Throws<BookingException>(() => Booking.Validate(ValidRequest() with { ServiceId = 0 }));
        Assert.Throws<BookingException>(() => Booking.Validate(ValidRequest() with { CustomerName = new string('a', 151) }));
        Assert.Throws<BookingException>(() => Booking.Validate(ValidRequest() with { CustomerPhone = new string('1', 31) }));
    }

    private static BookingRequest ValidRequest() => new(Guid.NewGuid(), 1, 1,
        new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero), "Client", "client@example.com", null, false);
}
