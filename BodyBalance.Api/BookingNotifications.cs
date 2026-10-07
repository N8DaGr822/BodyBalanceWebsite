using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace BodyBalance.Api;

public sealed class BookingNotifications(ScheduleData data, NotificationSender sender, IConfiguration configuration,
    ILogger<BookingNotifications> logger)
{
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
            var recipient = configuration[notification.Channel == "Email" ? "ContactRecipientEmail" : "ContactRecipientPhone"];
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
