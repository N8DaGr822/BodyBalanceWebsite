using System.Data;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed partial class ScheduleData
{
    public async Task<BookingReceipt> RequestBookingAsync(BookingRequest request, CancellationToken cancellationToken)
    {
        request = Booking.Validate(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        // Booking Transaction: All future approval/reschedule writers must use this same practitioner lock.
        using (var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @result;
            """, connection, transaction))
        {
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = $"BodyBalance.Practitioner.{request.PractitionerId}";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
                throw new BookingException("The schedule is busy. Please retry your request.", true);
        }

        // Retry Safety: Return the same receipt after a lost response without creating another request.
        using (var command = new SqlCommand("""
            SELECT PractitionerId, ServiceId, StartAtUtc, EndAtUtc, CustomerName, CustomerEmail,
                CustomerPhone, IsFirstVisitRequested, Status, ServiceName, TimeZoneId, Location
            FROM dbo.Appointments WHERE BookingRequestId = @requestId;
            """, connection, transaction))
        {
            command.Parameters.Add("@requestId", SqlDbType.UniqueIdentifier).Value = request.RequestId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(0) != request.PractitionerId || reader.GetInt32(1) != request.ServiceId
                    || reader.GetDateTime(2) != request.StartAtUtc.UtcDateTime
                    || reader.GetString(4) != request.CustomerName || reader.GetString(5) != request.CustomerEmail
                    || (reader.IsDBNull(6) ? null : reader.GetString(6)) != request.CustomerPhone
                    || reader.GetBoolean(7) != request.IsFirstVisit)
                    throw new BookingException("This request reference was already used. Refresh the page before starting a different request.", true);
                return new(request.RequestId, reader.GetString(8),
                    DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                    DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                    reader.GetString(9), reader.GetString(10), reader.GetString(11));
            }
        }

        var practitioner = (await GetPractitionersAsync(connection, transaction, cancellationToken))
            .Find(p => p.PractitionerId == request.PractitionerId);
        if (practitioner is null || string.IsNullOrWhiteSpace(practitioner.Location))
            throw new BookingException("This practitioner is not accepting online requests.", true);
        var service = (await GetServicesAsync(request.PractitionerId, connection, transaction, cancellationToken))
            .Find(s => s.ServiceId == request.ServiceId)
            ?? throw new BookingException("This service is no longer available. Choose another service.", true);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(practitioner.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(request.StartAtUtc.UtcDateTime, zone);
        var month = new DateTime(local.Year, local.Month, 1);
        var nowUtc = DateTime.UtcNow;
        var windows = await GetAvailabilityAsync(practitioner, month, connection, transaction, nowUtc, cancellationToken);
        var slot = Availability.Slots(windows, zone, service.DurationMinutes, practitioner.SlotIntervalMinutes)
            .Find(s => s.StartAtUtc == request.StartAtUtc.UtcDateTime)
            ?? throw new BookingException("This time is no longer available or does not meet the advance-notice requirement. Choose another time.", true);

        bool hasHistory;
        using (var command = new SqlCommand("""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Appointments
                WHERE PractitionerId = @id AND LOWER(LTRIM(RTRIM(CustomerEmail))) = @email
                    AND Status IN ('Requested', 'Confirmed', 'Completed', 'NoShow'))
                THEN 1 ELSE 0 END;
            """, connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.Int).Value = request.PractitionerId;
            command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = request.CustomerEmail;
            hasHistory = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }

        using (var command = new SqlCommand("""
            INSERT dbo.Appointments (PractitionerId, ServiceId, CustomerName, CustomerEmail, CustomerPhone,
                StartAtUtc, EndAtUtc, ServiceName, Price, CurrencyCode, Location, TimeZoneId,
                Status, BookingRequestId, IsFirstVisitRequested, CreatedAtUtc)
            VALUES (@practitionerId, @serviceId, @name, @email, @phone, @start, @end, @serviceName,
                @price, @currency, @location, @zone, 'Requested', @requestId, @firstVisit, @created);
            """, connection, transaction))
        {
            command.Parameters.Add("@practitionerId", SqlDbType.Int).Value = request.PractitionerId;
            command.Parameters.Add("@serviceId", SqlDbType.Int).Value = request.ServiceId;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 150).Value = request.CustomerName;
            command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = request.CustomerEmail;
            command.Parameters.Add("@phone", SqlDbType.NVarChar, 30).Value = (object?)request.CustomerPhone ?? DBNull.Value;
            command.Parameters.Add("@start", SqlDbType.DateTime2).Value = slot.StartAtUtc;
            command.Parameters.Add("@end", SqlDbType.DateTime2).Value = slot.EndAtUtc;
            command.Parameters.Add("@serviceName", SqlDbType.NVarChar, 150).Value = service.Name;
            var price = command.Parameters.Add("@price", SqlDbType.Decimal);
            price.Precision = 10;
            price.Scale = 2;
            price.Value = Booking.Price(service, request.IsFirstVisit, hasHistory);
            command.Parameters.Add("@currency", SqlDbType.Char, 3).Value = service.CurrencyCode;
            command.Parameters.Add("@location", SqlDbType.NVarChar, 500).Value = practitioner.Location;
            command.Parameters.Add("@zone", SqlDbType.NVarChar, 100).Value = practitioner.TimeZoneId;
            command.Parameters.Add("@requestId", SqlDbType.UniqueIdentifier).Value = request.RequestId;
            command.Parameters.Add("@firstVisit", SqlDbType.Bit).Value = request.IsFirstVisit;
            // Notice Snapshot: Persist the validation instant without rounding forward in datetime2(0).
            command.Parameters.Add("@created", SqlDbType.DateTime2).Value = new DateTime(
                nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        // Privacy: Do not reveal whether an entered email has prior visits through the public response.
        return new(request.RequestId, "Requested", slot.StartAtUtc, slot.EndAtUtc,
            service.Name, practitioner.TimeZoneId, practitioner.Location);
    }
}
