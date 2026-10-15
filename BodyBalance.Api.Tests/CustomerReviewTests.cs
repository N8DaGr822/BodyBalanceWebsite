using Xunit;

namespace BodyBalance.Api.Tests;

public class CustomerReviewTests
{
    private static CustomerReviewRequest Valid() => new(Guid.NewGuid(), "Alex", "A welcoming experience.", 1);

    [Fact]
    public void TrimsPublicTextWithoutChangingSubmissionIdentity()
    {
        var request = Valid() with { Name = "  Alex  ", Message = "  A welcoming experience.  " };
        var result = CustomerReview.Validate(request);
        Assert.Equal("Alex", result.Name);
        Assert.Equal("A welcoming experience.", result.Message);
        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.PractitionerId, result.PractitionerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsMissingPublicText(string? value)
    {
        Assert.Throws<BookingException>(() => CustomerReview.Validate(Valid() with { Name = value }));
        Assert.Throws<BookingException>(() => CustomerReview.Validate(Valid() with { Message = value }));
    }

    [Fact]
    public void EnforcesStorageLimits()
    {
        CustomerReview.Validate(Valid() with { Name = new string('a', 150), Message = new string('b', 2000) });
        Assert.Throws<BookingException>(() => CustomerReview.Validate(Valid() with { Name = new string('a', 151) }));
        Assert.Throws<BookingException>(() => CustomerReview.Validate(Valid() with { Message = new string('b', 2001) }));
    }

    [Fact]
    public void RequiresSubmissionReferenceAndPractitioner()
    {
        Assert.Throws<BookingException>(() => CustomerReview.Validate(Valid() with { RequestId = Guid.Empty }));
        Assert.Throws<BookingException>(() => CustomerReview.Validate(Valid() with { PractitionerId = 0 }));
        Assert.Throws<BookingException>(() => CustomerReview.Validate(Valid() with { PractitionerId = -1 }));
    }
}
