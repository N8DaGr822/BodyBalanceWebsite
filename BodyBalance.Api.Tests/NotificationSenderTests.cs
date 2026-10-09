using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BodyBalance.Api.Tests;

public class NotificationSenderTests
{
    private const string Key = "test-key-with-at-least-thirty-two-bytes";

    [Fact]
    public async Task EmailGoesOnlyToTheChosenRecipientAndPreservesPrivateReviewLink()
    {
        var notification = Notification("Email");
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://api.resend.com/emails", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-only-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("to").GetArrayLength());
            Assert.Equal("practitioner@example.com", root.GetProperty("to")[0].GetString());
            Assert.False(root.TryGetProperty("reply_to", out _));
            var text = root.GetProperty("text").GetString()!;
            Assert.Contains($"https://example.com/review-request#{notification.RequestId:D}.", text);
            Assert.DoesNotContain("CustomerEmail", text);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794\"}") };
        }));
        var result = await new NotificationSender(http, Config()).SendAsync(notification, default);
        Assert.Equal("Accepted", result.State);
        Assert.Equal("49a3999c-0ce1-4ea6-ab68-afcd6dc2e794", result.ProviderId);
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
            if (request.RequestUri!.Host == "api.resend.com")
            {
                Assert.Contains("tester@example.com", body);
                Assert.DoesNotContain("practitioner@example.com", body);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794\"}") };
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
    [InlineData(202, "NeedsReview")]
    [InlineData(401, "NeedsReview")]
    [InlineData(403, "NeedsReview")]
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
    [InlineData("{\"id\":\"\"}", "EmailNotAccepted")]
    [InlineData("{\"id\":\" \"}", "EmailNotAccepted")]
    [InlineData("{\"id\":null}", "EmailNotAccepted")]
    [InlineData("{}", "ProviderOutcomeUnknown")]
    [InlineData("{\"id\":123}", "ProviderOutcomeUnknown")]
    [InlineData("not-json", "ProviderOutcomeUnknown")]
    public async Task EmailSuccessWithoutAUsableProviderIdNeedsReview(string body, string expected)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body) })));
        var result = await new NotificationSender(http, Config()).SendAsync(Notification("Email"), default);
        Assert.Equal("NeedsReview", result.State);
        Assert.Null(result.ProviderId);
        Assert.Equal(expected, result.Error);
    }

    [Theory]
    [InlineData("ResendApiKey", "", "EmailNotConfigured")]
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
            Assert.Equal("practice@example.com", root.GetProperty("to")[0].GetString());
            Assert.Equal("Return to Self Reiki <sender@example.com>", root.GetProperty("from").GetString());
            Assert.Equal("client@example.com", root.GetProperty("reply_to").GetString());
            Assert.Equal("Contact subject", root.GetProperty("subject").GetString());
            Assert.Equal("Contact text", root.GetProperty("text").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794\"}") };
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
        ["ResendApiKey"] = "test-only-key", ["NotificationFromEmail"] = "sender@example.com",
        ["TwilioAccountSid"] = "AC" + new string('1', 32), ["TwilioApiKeySid"] = "SK" + new string('2', 32),
        ["TwilioApiKeySecret"] = "test-only-secret", ["NotificationFromPhone"] = "+15550000000"
    }).Build();

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
