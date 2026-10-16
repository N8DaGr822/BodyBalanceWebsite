using System.Data;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed record BookingNotification(long AppointmentId, Guid RequestId, int PractitionerId, string Channel,
    string? Recipient, Guid ClaimId, int Attempts, byte[] ReviewTokenHash, string Kind = "Request", NotificationMessage? Confirmation = null);

public sealed partial class ScheduleData
{
    public async Task<BookingNotification?> ClaimNotificationAsync(Guid? requestId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // Outbox Claim: One atomic update prevents simultaneous HTTP retries from sending the same channel.
        using var command = new SqlCommand("""
            ;WITH candidate AS
            (
                SELECT TOP (1) n.* FROM dbo.BookingNotifications n WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                JOIN dbo.Appointments a ON a.AppointmentId = n.AppointmentId
                -- Dispatch Timing: Match datetime2(0) rounding so newly inserted notifications are immediately eligible.
                WHERE n.State = 'Pending' AND n.Attempts < 5 AND n.NextAttemptAtUtc <= CAST(SYSUTCDATETIME() AS datetime2(0))
                    AND ((n.Kind = 'Request' AND a.Status = 'Requested' AND a.ReviewExpiresAtUtc > SYSUTCDATETIME())
                        OR (n.Kind = 'Confirmation' AND a.Status = 'Confirmed' AND a.StartAtUtc > SYSUTCDATETIME())
                        OR (n.Kind = 'Cancellation' AND a.Status = 'Cancelled'))
                    AND (@requestId IS NULL OR a.BookingRequestId = @requestId)
                ORDER BY n.NextAttemptAtUtc, n.AppointmentId, n.Channel
            )
            UPDATE candidate SET State = 'Sending', ClaimId = NEWID(), ClaimedAtUtc = SYSUTCDATETIME(), Attempts = Attempts + 1
                OUTPUT inserted.AppointmentId, inserted.Channel, inserted.ClaimId, inserted.Attempts, inserted.Kind;
            """, connection);
        command.Parameters.Add("@requestId", SqlDbType.UniqueIdentifier).Value = (object?)requestId ?? DBNull.Value;
        long appointmentId;
        string channel;
        Guid claimId;
        int attempts;
        string kind;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            appointmentId = reader.GetInt64(0);
            channel = reader.GetString(1);
            claimId = reader.GetGuid(2);
            attempts = reader.GetInt32(3);
            kind = reader.GetString(4);
        }
        using var details = new SqlCommand("""
            SELECT a.BookingRequestId, a.PractitionerId, p.NotificationEmail, p.NotificationPhone, a.ReviewTokenHash,
                p.DisplayName, a.CustomerName, a.CustomerEmail, a.ServiceName, a.StartAtUtc, a.EndAtUtc,
                a.TimeZoneId, a.Price, a.CurrencyCode, a.Location
            FROM dbo.Appointments a JOIN dbo.Practitioners p ON p.PractitionerId = a.PractitionerId
            WHERE a.AppointmentId = @id;
            """, connection);
        details.Parameters.Add("@id", SqlDbType.BigInt).Value = appointmentId;
        await using var result = await details.ExecuteReaderAsync(cancellationToken);
        if (!await result.ReadAsync(cancellationToken)) return null;
        var recipientColumn = channel == "Email" ? 2 : 3;
        NotificationMessage? confirmation = null;
        if (kind is "Confirmation" or "Cancellation")
        {
            var confirmationDetails = new BookingReviewDetails(result.GetGuid(0), result.GetString(5), result.GetString(6),
                result.GetString(7), null, result.GetString(8),
                DateTime.SpecifyKind(result.GetDateTime(9), DateTimeKind.Utc),
                DateTime.SpecifyKind(result.GetDateTime(10), DateTimeKind.Utc), result.GetString(11),
                result.GetDecimal(12), result.GetString(13), result.GetString(14), false, "Confirmed");
            confirmation = kind == "Cancellation" ? BookingReview.CancellationMessage(confirmationDetails)
                : BookingReview.ConfirmationMessage(confirmationDetails);
        }
        return new(appointmentId, result.GetGuid(0), result.GetInt32(1), channel,
            result.IsDBNull(recipientColumn) ? null : result.GetString(recipientColumn), claimId, attempts,
            result.IsDBNull(4) ? [] : (byte[])result[4], kind, confirmation);
    }

    public async Task FinishNotificationAsync(BookingNotification notification, NotificationResult result, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            UPDATE dbo.BookingNotifications SET State = @state, ProviderMessageId = @providerId,
                LastError = @error, AcceptedAtUtc = CASE WHEN @state = 'Accepted' THEN SYSUTCDATETIME() ELSE NULL END,
                NextAttemptAtUtc = DATEADD(minute, 15, SYSUTCDATETIME())
            WHERE AppointmentId = @id AND Channel = @channel AND Kind = @kind AND State = 'Sending' AND ClaimId = @claimId;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = notification.AppointmentId;
        command.Parameters.Add("@channel", SqlDbType.VarChar, 5).Value = notification.Channel;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 12).Value = notification.Kind;
        command.Parameters.Add("@claimId", SqlDbType.UniqueIdentifier).Value = notification.ClaimId;
        command.Parameters.Add("@state", SqlDbType.VarChar, 12).Value = result.State == "Pending" && notification.Attempts >= 5
            ? "NeedsReview" : result.State;
        command.Parameters.Add("@providerId", SqlDbType.NVarChar, 100).Value = (object?)result.ProviderId ?? DBNull.Value;
        command.Parameters.Add("@error", SqlDbType.VarChar, 100).Value = (object?)result.Error ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
