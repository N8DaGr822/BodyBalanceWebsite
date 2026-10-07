using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace BodyBalance.Api;

public sealed record NotificationResult(string State, string? ProviderId = null, string? Error = null);
public sealed record NotificationMessage(string Channel, string? Recipient, string Subject,
    string EmailText, string SmsText, string? ReplyTo = null);

public sealed class NotificationSender(HttpClient http, IConfiguration configuration)
{
    public async Task<NotificationResult> SendAsync(BookingNotification notification, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["BookingSiteUrl"];
        var signingKey = configuration["BookingReviewSigningKey"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var site) || site.Scheme != "https"
            || !string.IsNullOrEmpty(site.UserInfo) || site.AbsolutePath != "/" || site.Query != "" || site.Fragment != ""
            || string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
            return new("Pending", Error: "ReviewLinkNotConfigured");
        var token = BookingReview.CreateToken(notification.RequestId, notification.PractitionerId, signingKey);
        if (!CryptographicOperations.FixedTimeEquals(BookingReview.TokenHash(token)!, notification.ReviewTokenHash))
            return new("NeedsReview", Error: "SigningKeyChanged");
        // Link Privacy: URL fragments stay out of HTTP request paths and referrer headers.
        var link = new Uri(site, $"review-request#{notification.RequestId:D}.{token}").AbsoluteUri;
        return await SendAsync(new NotificationMessage(notification.Channel, notification.Recipient,
            "Appointment request awaiting your approval",
            $"A client requested an appointment with you. Review the details and approve or decline:\n{link}\n\nThis private link expires after 7 days or when the session starts. Keep it private. You can also sign in at {new Uri(site, "practitioner")} to review requests.",
            $"Return to Self Reiki: New appointment request. Review privately: {link} Reply STOP to unsubscribe."), cancellationToken);
    }

    // Shared Delivery: Booking requests and contact messages use the same providers, test routing and failure policy.
    public async Task<NotificationResult> SendAsync(NotificationMessage notification, CancellationToken cancellationToken)
    {
        var testEmail = configuration["NotificationTestEmail"];
        var testPhone = configuration["NotificationTestPhone"];
        if (string.IsNullOrWhiteSpace(testEmail) != string.IsNullOrWhiteSpace(testPhone))
            return new("Pending", Error: "IncompleteTestRecipients");
        var recipient = (notification.Channel == "Email" ? testEmail : testPhone) ?? notification.Recipient;
        if (string.IsNullOrWhiteSpace(recipient)) recipient = notification.Recipient;

        using var request = new HttpRequestMessage(HttpMethod.Post, notification.Channel == "Email"
            ? "https://api.sendgrid.com/v3/mail/send"
            : $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(configuration["TwilioAccountSid"] ?? "")}/Messages.json");
        if (notification.Channel == "Email")
        {
            var apiKey = configuration["SendGridApiKey"];
            var from = configuration["NotificationFromEmail"];
            if (string.IsNullOrWhiteSpace(apiKey) || !ValidEmail(from) || !ValidEmail(recipient))
                return new("Pending", Error: "EmailNotConfigured");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new
            {
                personalizations = new[] { new { to = new[] { new { email = recipient } } } },
                from = new { email = from, name = "Return to Self Reiki" },
                subject = notification.Subject,
                reply_to = ValidEmail(notification.ReplyTo) ? new { email = notification.ReplyTo } : null,
                content = new[] { new { type = "text/plain", value = notification.EmailText } },
                // Tracking: Do not let a tracking redirect rewrite or retain the private approval link.
                tracking_settings = new { click_tracking = new { enable = false, enable_text = false }, open_tracking = new { enable = false } }
            }, options: new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        }
        else if (notification.Channel == "Sms")
        {
            var account = configuration["TwilioAccountSid"];
            var key = configuration["TwilioApiKeySid"];
            var secret = configuration["TwilioApiKeySecret"];
            var from = configuration["NotificationFromPhone"];
            if (account is null || !Regex.IsMatch(account, "^AC[0-9a-fA-F]{32}$")
                || key is null || !Regex.IsMatch(key, "^SK[0-9a-fA-F]{32}$") || string.IsNullOrWhiteSpace(secret)
                || !ValidPhone(from) || !ValidPhone(recipient)) return new("Pending", Error: "SmsNotConfigured");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}")));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = from!, ["To"] = recipient!,
                ["Body"] = notification.SmsText
            });
        }
        else return new("NeedsReview", Error: "UnknownChannel");

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new("Pending", Error: "ProviderRateLimited");
            if (notification.Channel == "Email" && response.StatusCode == HttpStatusCode.Accepted)
                return new("Accepted", response.Headers.TryGetValues("X-Message-Id", out var values) ? values.FirstOrDefault() : null);
            if (notification.Channel == "Sms" && response.StatusCode == HttpStatusCode.Created)
            {
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var status = json.RootElement.GetProperty("status").GetString();
                var id = json.RootElement.GetProperty("sid").GetString();
                return !string.IsNullOrEmpty(id) && status is "accepted" or "queued" or "sending" or "sent" or "delivered"
                    ? new("Accepted", id) : new("NeedsReview", id, "SmsNotAccepted");
            }
            // Delivery Safety: An uncertain result is never blindly resent; operators reconcile provider logs first.
            return new("NeedsReview", Error: $"ProviderHttp{(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new("NeedsReview", Error: "ProviderOutcomeUnknown");
        }
    }

    private static bool ValidEmail(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && new EmailAddressAttribute().IsValid(value);
    private static bool ValidPhone(string? value) => value is not null && Regex.IsMatch(value, "^\\+[1-9][0-9]{7,14}$");
}
