using System.Data;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed partial class ScheduleData
{
    public async Task<BookingReviewDetails?> GetBookingReviewAsync(ReviewAccess access, CancellationToken cancellationToken,
        int? authorizedPractitionerId = null)
    {
        if (access.RequestId == Guid.Empty || authorizedPractitionerId is null && BookingReview.TokenHash(access.Token) is null) return null;
        await using var connection = await OpenAsync(cancellationToken);
        return (await ReadReviewAsync(access, connection, null, authorizedPractitionerId, cancellationToken))?.Details;
    }

    public async Task<BookingReviewDetails?> DecideBookingAsync(BookingDecision decision, CancellationToken cancellationToken,
        int? authorizedPractitionerId = null)
    {
        var access = new ReviewAccess(decision.RequestId, decision.Token);
        if (access.RequestId == Guid.Empty || authorizedPractitionerId is null && BookingReview.TokenHash(access.Token) is null) return null;
        await using var connection = await OpenAsync(cancellationToken);
        var initial = await ReadReviewAsync(access, connection, null, authorizedPractitionerId, cancellationToken);
        if (initial is null) return null;
        decision = BookingReview.Validate(decision);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        // Approval Transaction: Serialize with submissions and the administrator's review script.
        using (var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @result;
            """, connection, transaction))
        {
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = $"BodyBalance.Practitioner.{initial.PractitionerId}";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
                throw new BookingException("The schedule is busy. Please retry this decision.", true);
        }
        var current = await ReadReviewAsync(access, connection, transaction, authorizedPractitionerId, cancellationToken);
        if (current is null) return null;
        var details = current.Details;
        if (details.Status != "Requested")
        {
            // Lost Response: Repeating the same decision is safe, but a used link cannot change a decision.
            if (details.Status == decision.Status && (decision.Status == "Cancelled"
                || details.Price == decision.Price && details.Location == decision.Location)) return details;
            throw new BookingException("This request has already been reviewed. Refresh to see its status.", true);
        }
        if (decision.Status == "Confirmed")
        {
            var practitioner = (await GetPractitionersAsync(connection, transaction, cancellationToken))
                .Find(p => p.PractitionerId == current.PractitionerId)
                ?? throw new BookingException("This practitioner is no longer active.", true);
            var service = (await GetServicesAsync(current.PractitionerId, connection, transaction, cancellationToken))
                .Find(s => s.ServiceId == current.ServiceId)
                ?? throw new BookingException("This session is no longer offered.", true);
            if (details.EndAtUtc - details.StartAtUtc != TimeSpan.FromMinutes(service.DurationMinutes)
                || details.StartAtUtc < current.CreatedAtUtc.AddHours(practitioner.MinimumNoticeHours))
                throw new BookingException("The session duration or notice requirement has changed. Arrange a new request with the client.", true);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(practitioner.TimeZoneId);
            var local = TimeZoneInfo.ConvertTimeFromUtc(details.StartAtUtc, zone);
            // Notice Policy: Submission met the lead time; approval may happen within that notice period.
            var windows = await GetAvailabilityAsync(practitioner with { MinimumNoticeHours = 0 },
                new DateTime(local.Year, local.Month, 1), connection, transaction, DateTime.UtcNow, cancellationToken);
            if (!Availability.Slots(windows, zone, service.DurationMinutes, practitioner.SlotIntervalMinutes)
                .Any(s => s.StartAtUtc == details.StartAtUtc && s.EndAtUtc == details.EndAtUtc))
                throw new BookingException("This time is no longer available. Contact the client to arrange another time.", true);
        }
        using (var command = new SqlCommand("""
            UPDATE dbo.Appointments SET Status = @status, ReviewedAtUtc = SYSUTCDATETIME(),
                Price = CASE WHEN @status = 'Confirmed' THEN @price ELSE Price END,
                Location = CASE WHEN @status = 'Confirmed' THEN @location ELSE Location END
            WHERE BookingRequestId = @requestId AND Status = 'Requested';
            """, connection, transaction))
        {
            command.Parameters.Add("@requestId", SqlDbType.UniqueIdentifier).Value = decision.RequestId;
            command.Parameters.Add("@status", SqlDbType.VarChar, 12).Value = decision.Status!;
            var price = command.Parameters.Add("@price", SqlDbType.Decimal);
            price.Precision = 10;
            price.Scale = 2;
            price.Value = (object?)decision.Price ?? DBNull.Value;
            command.Parameters.Add("@location", SqlDbType.NVarChar, 500).Value = (object?)decision.Location ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return details with { Status = decision.Status!, Price = decision.Price ?? details.Price, Location = decision.Location ?? details.Location };
    }

    private static async Task<ReviewRow?> ReadReviewAsync(ReviewAccess access, SqlConnection connection,
        SqlTransaction? transaction, int? authorizedPractitionerId, CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("""
            SELECT a.PractitionerId, a.ServiceId, a.CreatedAtUtc, p.DisplayName, a.CustomerName,
                a.CustomerEmail, a.CustomerPhone, a.ServiceName, a.StartAtUtc, a.EndAtUtc,
                a.TimeZoneId, a.Price, a.CurrencyCode, a.Location, a.IsFirstVisitRequested, a.Status
            FROM dbo.Appointments a JOIN dbo.Practitioners p ON p.PractitionerId = a.PractitionerId
            WHERE a.BookingRequestId = @requestId
                AND (a.PractitionerId = @authorizedPractitionerId
                    OR (a.ReviewTokenHash = @hash AND a.ReviewExpiresAtUtc > SYSUTCDATETIME()));
            """, connection, transaction);
        command.Parameters.Add("@requestId", SqlDbType.UniqueIdentifier).Value = access.RequestId;
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = (object?)BookingReview.TokenHash(access.Token) ?? DBNull.Value;
        command.Parameters.Add("@authorizedPractitionerId", SqlDbType.Int).Value = (object?)authorizedPractitionerId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.GetInt32(0), reader.GetInt32(1), DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            new(access.RequestId, reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc), reader.GetString(10),
                reader.GetDecimal(11), reader.GetString(12), reader.GetString(13), reader.GetBoolean(14), reader.GetString(15)));
    }

    private sealed record ReviewRow(int PractitionerId, int ServiceId, DateTime CreatedAtUtc, BookingReviewDetails Details);

    public async Task<int?> GetBookingPractitionerAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("SELECT PractitionerId FROM dbo.Appointments WHERE BookingRequestId = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int id ? id : null;
    }

    public async Task<List<BookingSummary>> GetPractitionerRequestsAsync(int practitionerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            SELECT * FROM (
                SELECT TOP (100) BookingRequestId, CustomerName, ServiceName, StartAtUtc, TimeZoneId, Status
                FROM dbo.Appointments WHERE PractitionerId = @id AND BookingRequestId IS NOT NULL AND Status = 'Requested'
                ORDER BY StartAtUtc, BookingRequestId
            ) pending
            UNION ALL
            SELECT * FROM (
                SELECT TOP (100) BookingRequestId, CustomerName, ServiceName, StartAtUtc, TimeZoneId, Status
                FROM dbo.Appointments WHERE PractitionerId = @id AND BookingRequestId IS NOT NULL
                    AND Status = 'Confirmed' AND EndAtUtc > SYSUTCDATETIME()
                ORDER BY StartAtUtc, BookingRequestId
            ) confirmed;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = practitionerId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BookingSummary>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), practitionerId, reader.GetString(1), reader.GetString(2),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc), reader.GetString(4), reader.GetString(5)));
        return result;
    }
}

public sealed record BookingSummary(Guid RequestId, int PractitionerId, string CustomerName, string ServiceName,
    DateTime StartAtUtc, string TimeZoneId, string Status);
