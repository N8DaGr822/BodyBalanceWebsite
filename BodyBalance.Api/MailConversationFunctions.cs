using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using MimeKit;

namespace BodyBalance.Api;

public sealed partial class ScheduleFunctions
{
    private async Task<bool> InboxAccessAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        var access = await AccessAsync(request, cancellationToken);
        // Shared Inbox: Only explicitly configured practitioners may read everyone's conversations.
        var sharedIds = (configuration["SharedInboxPractitionerIds"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return access.Allowed.Any(id => sharedIds.Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Function("Conversations")]
    public Task<HttpResponseData> Conversations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioner/conversations")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            if (!await InboxAccessAsync(request, cancellationToken)) return await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "Sign in with shared inbox access." });
            var offset = int.TryParse(request.Query["offset"], out var page) && page >= 0 ? page : 0;
            return await JsonAsync(request, HttpStatusCode.OK, await data.GetConversationsAsync(offset, cancellationToken));
        }, cancellationToken, "The inbox is temporarily unavailable.");

    [Function("ConversationMessages")]
    public Task<HttpResponseData> ConversationMessages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioner/conversations/{id:guid}")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            if (!await InboxAccessAsync(request, cancellationToken)) return await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "Sign in with shared inbox access." });
            var conversation = await data.GetConversationAsync(id, cancellationToken);
            if (conversation is null) return await JsonAsync(request, HttpStatusCode.NotFound, new { error = "Conversation not found." });
            var offset = int.TryParse(request.Query["offset"], out var page) && page >= 0 ? page : 0;
            return await JsonAsync(request, HttpStatusCode.OK, new { conversation, messages = await data.GetMailEntriesAsync(id, offset, cancellationToken) });
        }, cancellationToken, "The conversation is temporarily unavailable.");

    [Function("ReplyToConversation")]
    public Task<HttpResponseData> ReplyToConversation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner/conversations/{id:guid}/reply")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            if (!await InboxAccessAsync(request, cancellationToken)) return await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "Sign in with shared inbox access." });
            if (Header(request, "X-Booking-Review") != "1") return await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "Refresh the page before sending." });
            using var reader = new StreamReader(request.Body);
            var buffer = new char[65537];
            var count = await reader.ReadBlockAsync(buffer, cancellationToken);
            if (count > 65536) return await JsonAsync(request, HttpStatusCode.RequestEntityTooLarge, new { error = "Reply is too large." });
            var reply = ConversationMail.Validate(JsonSerializer.Deserialize<MailReply>(buffer.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new BookingException("Enter a reply."));
            var conversation = await data.GetConversationAsync(id, cancellationToken) ?? throw new BookingException("Conversation not found.");
            var sender = configuration["NotificationFromEmail"];
            if (!MailboxAddress.TryParse(sender, out var from) || from.Address != sender || string.IsNullOrWhiteSpace(configuration["ResendApiKey"]))
                throw new BookingException("Inbox sending is not configured yet.");
            if (!string.IsNullOrWhiteSpace(configuration["NotificationTestEmail"]) || !string.IsNullOrWhiteSpace(configuration["NotificationTestPhone"]))
                throw new BookingException("Inbox replies are paused while notification test routing is enabled.");
            if (!MailboxAddress.TryParse(conversation.CustomerEmail, out var recipient) || recipient.Address != conversation.CustomerEmail
                || string.Equals(sender, recipient.Address, StringComparison.OrdinalIgnoreCase))
                throw new BookingException("This message has no safe reply address. Review the original email.");
            var access = await AccessAsync(request, cancellationToken);
            var claim = await data.ClaimMailReplyAsync(id, reply, sender!, access.Identity!.UserDetails, cancellationToken);
            if (!claim.Claimed) return await JsonAsync(request, HttpStatusCode.OK, new { state = claim.State });
            var result = await notifications.SendInboxReplyAsync(conversation, reply, claim.InReplyTo, cancellationToken);
            await data.FinishMailReplyAsync(reply.MessageId, result, cancellationToken);
            return await JsonAsync(request, HttpStatusCode.OK, new { state = result.State == "Accepted" ? "Accepted" : "NeedsReview" });
        }, cancellationToken, "The send result could not be confirmed. Refresh the conversation before doing anything else.", "Enter a valid reply.");

    [Function("ReceiveConversationMail")]
    public Task<HttpResponseData> ReceiveConversationMail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "mail/incoming")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var timestamp = Header(request, "X-Inbox-Timestamp");
            var recipient = Header(request, "X-Inbox-Recipient");
            var signature = Header(request, "X-Inbox-Signature");
            if (signature is null || signature.Length != 64 || !string.Equals(recipient, configuration["ContactRecipientEmail"], StringComparison.OrdinalIgnoreCase))
                return request.CreateResponse(HttpStatusCode.Unauthorized);
            using var raw = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (raw.Length + count > ConversationMail.MaximumBytes) return request.CreateResponse(HttpStatusCode.RequestEntityTooLarge);
                raw.Write(buffer, 0, count);
            }
            var bytes = raw.ToArray();
            if (!ConversationMail.VerifySignature(configuration["InboxWebhookSecret"], timestamp, recipient, signature, bytes, DateTimeOffset.UtcNow))
                return request.CreateResponse(HttpStatusCode.Unauthorized);
            var mail = await ConversationMail.ParseAsync(bytes, cancellationToken);
            // Notification Loop: Website alerts are already stored as contact messages; Gmail still receives its copies.
            if (!string.Equals(mail.From, configuration["NotificationFromEmail"], StringComparison.OrdinalIgnoreCase))
                await data.SaveIncomingMailAsync(mail, bytes, cancellationToken);
            return request.CreateResponse(HttpStatusCode.NoContent);
        }, cancellationToken, "Incoming mail could not be stored.");

    [Function("DownloadOriginalMail")]
    public Task<HttpResponseData> DownloadOriginalMail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioner/mail/{id:guid}/original")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            if (!await InboxAccessAsync(request, cancellationToken)) return request.CreateResponse(HttpStatusCode.Forbidden);
            var raw = await data.GetOriginalMailAsync(id, cancellationToken);
            if (raw is null) return request.CreateResponse(HttpStatusCode.NotFound);
            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/octet-stream");
            response.Headers.Add("Content-Disposition", $"attachment; filename=message-{id:D}.eml");
            response.Headers.Add("Cache-Control", "no-store");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            await response.Body.WriteAsync(raw, cancellationToken);
            return response;
        }, cancellationToken);
}
