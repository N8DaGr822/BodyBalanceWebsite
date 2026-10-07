using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BodyBalance.Api.Tests;

public class NotificationSenderTests
{
    private const string Key = "test-key-with-at-least-thirty-two-bytes";

    [Fact]
    public async Task EmailGoesOnlyToTheChosenRecipientAndDisablesLinkTracking()
    {
        var notification = Notification("Email");
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://api.sendgrid.com/v3/mail/send", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal("practitioner@example.com", root.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
            Assert.False(root.GetProperty("tracking_settings").GetProperty("click_tracking").GetProperty("enable").GetBoolean());
            var text = root.GetProperty("content")[0].GetProperty("value").GetString()!;
            Assert.Contains($"https://example.com/review-request#{notification.RequestId:D}.", text);
            Assert.DoesNotContain("CustomerEmail", text);
            return new(HttpStatusCode.Accepted);
        }));
        Assert.Equal("Accepted", (await new NotificationSender(http, Config()).SendAsync(notification, default)).State);
    }

    [Fact]
    public async Task TestRoutingOverridesBothChannelsWithoutChangingPractitionerOwnership()
    {
        var configuration = Config();
        configuration["NotificationTestEmail"] = "tester@example.com";
        configuration["NotificationTestPhone"] = "+15551234567";
        using var http = new HttpClient(new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            if (request.RequestUri!.Host == "api.sendgrid.com")
            {
                Assert.Contains("tester@example.com", body);
                Assert.DoesNotContain("practitioner@example.com", body);
                return new(HttpStatusCode.Accepted);
            }
            Assert.Contains("To=%2B15551234567", body);
            Assert.DoesNotContain("To=%2B15559999999", body);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            return new(HttpStatusCode.Created) { Content = new StringContent("{\"sid\":\"SMtest\",\"status\":\"queued\"}") };
        }));
        var sender = new NotificationSender(http, configuration);
        Assert.Equal("Accepted", (await sender.SendAsync(Notification("Email"), default)).State);
        Assert.Equal("Accepted", (await sender.SendAsync(Notification("Sms") with { Recipient = "+15559999999" }, default)).State);
    }

    [Theory]
    [InlineData(429, "Pending")]
    [InlineData(401, "NeedsReview")]
    [InlineData(500, "NeedsReview")]
    public async Task RateLimitsRetryButRejectedOrUncertainOutcomesNeedReview(int status, string expected)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var result = await new NotificationSender(http, Config()).SendAsync(Notification("Email"), default);
        Assert.Equal(expected, result.State);
    }

    [Fact]
    public async Task ConnectionFailuresAreNotAutomaticallyRetriedAsPotentialDuplicateSends()
    {
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("Unknown outcome")));
        var result = await new NotificationSender(http, Config()).SendAsync(Notification("Email"), default);
        Assert.Equal("NeedsReview", result.State);
        Assert.Equal("ProviderOutcomeUnknown", result.Error);
    }

    [Theory]
    [InlineData("SendGridApiKey", "", "EmailNotConfigured")]
    [InlineData("BookingSiteUrl", "http://example.com", "ReviewLinkNotConfigured")]
    [InlineData("BookingSiteUrl", "https://example.com/path", "ReviewLinkNotConfigured")]
    [InlineData("NotificationTestEmail", "tester@example.com", "IncompleteTestRecipients")]
    [InlineData("BookingReviewSigningKey", "different-long-signing-key-for-this-test", "SigningKeyChanged")]
    public async Task InvalidConfigurationNeverSends(string key, string value, string expected)
    {
        var configuration = Config();
        configuration[key] = value;
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No HTTP request should occur.")));
        var result = await new NotificationSender(http, configuration).SendAsync(Notification("Email"), default);
        Assert.Equal(expected, result.Error);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("undelivered")]
    [InlineData("canceled")]
    [InlineData("unexpected-status")]
    [InlineData(null)]
    public async Task SmsProviderRejectionOrUnknownStatusIsNotReportedAsAccepted(string? status)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            { Content = new StringContent(JsonSerializer.Serialize(new { sid = "SMtest", status })) })));
        var result = await new NotificationSender(http, Config()).SendAsync(Notification("Sms") with { Recipient = "+15559999999" }, default);
        Assert.Equal("NeedsReview", result.State);
    }

    [Fact]
    public async Task ContactEmailUsesSharedDeliveryAndReplyToWithoutRequiringBookingLinkSettings()
    {
        var configuration = Config();
        configuration["BookingSiteUrl"] = "";
        configuration["BookingReviewSigningKey"] = "";
        using var http = new HttpClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal("practice@example.com", root.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
            Assert.Equal("sender@example.com", root.GetProperty("from").GetProperty("email").GetString());
            Assert.Equal("client@example.com", root.GetProperty("reply_to").GetProperty("email").GetString());
            Assert.Equal("Contact subject", root.GetProperty("subject").GetString());
            Assert.Equal("Contact text", root.GetProperty("content")[0].GetProperty("value").GetString());
            return new(HttpStatusCode.Accepted);
        }));
        var notification = new NotificationMessage("Email", "practice@example.com", "Contact subject", "Contact text", "Contact alert", "client@example.com");
        Assert.Equal("Accepted", (await new NotificationSender(http, configuration).SendAsync(notification, default)).State);
    }

    [Fact]
    public async Task ContactSmsUsesTestOverrideAndTheContactAlert()
    {
        var configuration = Config();
        configuration["NotificationTestEmail"] = "tester@example.com";
        configuration["NotificationTestPhone"] = "+15551234567";
        using var http = new HttpClient(new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("To=%2B15551234567", body);
            Assert.DoesNotContain("15559999999", body);
            Assert.Contains("Body=Contact+alert", body);
            Assert.DoesNotContain("Private+message", body);
            return new(HttpStatusCode.Created) { Content = new StringContent("{\"sid\":\"SMtest\",\"status\":\"queued\"}") };
        }));
        var notification = new NotificationMessage("Sms", "+15559999999", "Contact subject", "Private message", "Contact alert");
        Assert.Equal("Accepted", (await new NotificationSender(http, configuration).SendAsync(notification, default)).State);
    }

    private static BookingNotification Notification(string channel)
    {
        var requestId = Guid.NewGuid();
        return new(1, requestId, 1, channel, "practitioner@example.com", Guid.NewGuid(), 1,
            BookingReview.TokenHash(BookingReview.CreateToken(requestId, 1, Key))!);
    }

    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["BookingSiteUrl"] = "https://example.com", ["BookingReviewSigningKey"] = Key,
        ["SendGridApiKey"] = "test-only-key", ["NotificationFromEmail"] = "sender@example.com",
        ["TwilioAccountSid"] = "AC" + new string('1', 32), ["TwilioApiKeySid"] = "SK" + new string('2', 32),
        ["TwilioApiKeySecret"] = "test-only-secret", ["NotificationFromPhone"] = "+15550000000"
    }).Build();

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
