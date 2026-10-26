using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed partial class ScheduleData
{
    public async Task<BookingReviewDetails?> RescheduleBookingAsync(Guid requestId, BookingTimeChange change,
        int authorizedPractitionerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        // Schedule Transaction: Use the same practitioner lock as approvals, bookings, and availability edits.
        using (var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @result;
            """, connection, transaction))
        {
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = $"BodyBalance.Practitioner.{authorizedPractitionerId}";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
                throw new BookingException("The schedule is busy. Please retry this change.", true);
        }
        var current = await ReadReviewAsync(new(requestId, null), connection, transaction, authorizedPractitionerId, cancellationToken);
        if (current is null) return null;
        var details = current.Details;
        if (details.Status is not ("Requested" or "Confirmed"))
            throw new BookingException("Only pending requests and confirmed appointments can be adjusted. Refresh to see the current status.", true);
        var start = BookingReview.ChangedStartUtc(change, details.TimeZoneId);
        // Lost Response: A replay of the immediately preceding change returns without another update or email.
        if (details.ScheduleRevision == (long)change.ExpectedRevision + 1 && details.StartAtUtc == start)
            return details;
        if (details.ScheduleRevision != change.ExpectedRevision)
            throw new BookingException("This appointment was changed elsewhere. Refresh before editing it again.", true);
        if (start == details.StartAtUtc) return details;
        var now = DateTime.UtcNow;
        if (details.StartAtUtc <= now || start <= now)
            throw new BookingException("Only future appointments can be moved to another future time.");
        var practitioner = (await GetPractitionersAsync(connection, transaction, cancellationToken))
            .Find(p => p.PractitionerId == authorizedPractitionerId)
            ?? throw new BookingException("This practitioner is no longer active.", true);
        var duration = details.EndAtUtc - details.StartAtUtc;
        var end = start.Add(duration);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(details.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(start, zone);
        // Admin Adjustment: Preserve booked duration and allow short-notice changes, within available working hours.
        var windows = await GetAvailabilityAsync(practitioner with { MinimumNoticeHours = 0 },
            new DateTime(local.Year, local.Month, 1), connection, transaction, now, cancellationToken, requestId);
        // Admin Adjustment: Allow any minute when the entire session fits in an available window.
        if (!windows.Any(w => start >= w.StartAtUtc && end <= w.EndAtUtc))
            throw new BookingException("This time is unavailable. Choose a time within working hours that does not overlap another appointment or time off.", true);
        var updated = details with { StartAtUtc = start, EndAtUtc = end, ScheduleRevision = checked(details.ScheduleRevision + 1) };
        using (var command = new SqlCommand("""
            UPDATE dbo.Appointments SET StartAtUtc = @start, EndAtUtc = @end, ScheduleRevision = @revision,
                ReviewedAtUtc = SYSUTCDATETIME()
            WHERE BookingRequestId = @requestId AND PractitionerId = @practitionerId AND Status IN ('Requested', 'Confirmed');
            -- Update Outbox: Snapshot the email with the saved change; subsequent edits cannot alter its payload.
            INSERT dbo.BookingNotifications(AppointmentId, Channel, Kind, Revision, MessageJson)
            SELECT AppointmentId, 'Email', 'Rescheduled', @revision, @message
            FROM dbo.Appointments WHERE BookingRequestId = @requestId AND Status = 'Confirmed';
            """, connection, transaction))
        {
            command.Parameters.Add("@requestId", SqlDbType.UniqueIdentifier).Value = requestId;
            command.Parameters.Add("@practitionerId", SqlDbType.Int).Value = authorizedPractitionerId;
            command.Parameters.Add("@start", SqlDbType.DateTime2).Value = start;
            command.Parameters.Add("@end", SqlDbType.DateTime2).Value = end;
            command.Parameters.Add("@revision", SqlDbType.Int).Value = updated.ScheduleRevision;
            command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(BookingReview.RescheduledMessage(updated));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }
}
