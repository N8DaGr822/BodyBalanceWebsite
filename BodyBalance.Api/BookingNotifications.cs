using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace BodyBalance.Api;

public sealed class BookingNotifications(ScheduleData data, NotificationSender sender, IConfiguration configuration,
    ILogger<BookingNotifications> logger)
{
    public Task<NotificationResult> SendInboxReplyAsync(MailConversation conversation, MailReply reply, string? inReplyTo,
        CancellationToken cancellationToken) => sender.SendAsync(new NotificationMessage("Email", conversation.CustomerEmail,
            $"Re: {conversation.Subject} [EW:{conversation.ConversationId:N}]", reply.Body!, "",
            configuration["NotificationFromEmail"], $"inbox/{reply.MessageId:D}",
            inReplyTo is null ? null : new() { ["In-Reply-To"] = $"<{inReplyTo}>", ["References"] = $"<{inReplyTo}>" }),
            cancellationToken);

    public Task<NotificationResult> SendSignInCodeAsync(string email, string code, CancellationToken cancellationToken) =>
        // Authentication Delivery: Codes must never be redirected by notification test settings.
        sender.SendAsync(new NotificationMessage("Email", email, "Your Entwined & Unwind sign-in code",
            $"Your sign-in code is: {code}\n\nEnter it on the Entwined & Unwind sign-in page. It expires in 10 minutes and can be used once.\nIf you did not request this code, ignore this email. Do not share the code.", ""),
            cancellationToken, applyTestRouting: false);

    public async Task<int> DispatchAsync(Guid? requestId, CancellationToken cancellationToken)
    {
        var processed = 0;
        // Managed API: Bounded HTTP work only; durable rows are retried by the scheduled dispatch workflow.
        for (var i = 0; i < 2; i++)
        {
            var notification = await data.ClaimNotificationAsync(requestId, cancellationToken);
            if (notification is null) break;
            var result = await sender.SendAsync(notification, cancellationToken);
            await data.FinishNotificationAsync(notification, result, cancellationToken);
            if (result.State != "Accepted")
                logger.LogWarning("Booking notification {AppointmentId}/{Channel}: {State}, {Reason}",
                    notification.AppointmentId, notification.Channel, result.State, result.Error);
            processed++;
        }
        return processed;
    }

    public async Task<int> DispatchContactAsync(Guid? requestId, CancellationToken cancellationToken)
    {
        var processed = 0;
        for (var i = 0; i < 2; i++)
        {
            var notification = await data.ClaimContactNotificationAsync(requestId, cancellationToken);
            if (notification is null) break;
            var recipient = ContactMessage.Recipient(notification,
                configuration[notification.Channel == "Email" ? "ContactRecipientEmail" : "ContactRecipientPhone"]);
            var result = await sender.SendAsync(ContactMessage.Notification(notification, recipient), cancellationToken);
            await data.FinishContactNotificationAsync(notification, result, cancellationToken);
            if (result.State != "Accepted")
                logger.LogWarning("Contact notification {RequestId}/{Channel}: {State}, {Reason}",
                    notification.RequestId, notification.Channel, result.State, result.Error);
            processed++;
        }
        return processed;
    }
}
