using System.Data;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed partial class ScheduleData
{
    public async Task<AvailabilitySettings> GetAvailabilitySettingsAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var practitioner = (await GetPractitionersAsync(connection, null, cancellationToken)).Find(p => p.PractitionerId == id)
            ?? throw new BookingException("This practitioner is no longer active.");
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(practitioner.TimeZoneId)).Date;
        var rules = await ReadWorkingHoursAsync(id, today, connection, null, cancellationToken);
        using var command = new SqlCommand("""
            SELECT AvailabilityExceptionId, StartAtUtc, EndAtUtc FROM dbo.AvailabilityExceptions
            WHERE PractitionerId = @id AND Kind = 'Blocked' AND EndAtUtc > SYSUTCDATETIME()
            ORDER BY StartAtUtc, AvailabilityExceptionId;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        var timeOff = new List<TimeOffEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            timeOff.Add(new(reader.GetInt32(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)));
        return new(practitioner.TimeZoneId, today, PractitionerAvailability.Version(rules), rules, timeOff.ToArray());
    }

    public async Task ChangeAvailabilityAsync(int id, WeeklyHoursChange? hours, TimeOffChange? timeOff, int? removeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        // Availability Transaction: Share the booking/approval lock so time off cannot race a confirmation.
        using (var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @result;
            """, connection, transaction))
        {
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = $"BodyBalance.Practitioner.{id}";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
                throw new BookingException("The schedule is busy. Please retry.", true);
        }
        var practitioner = (await GetPractitionersAsync(connection, transaction, cancellationToken)).Find(p => p.PractitionerId == id)
            ?? throw new BookingException("This practitioner is no longer active.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(practitioner.TimeZoneId);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        if (hours is not null)
        {
            var rules = PractitionerAvailability.ValidateHours(hours.Hours, today);
            var existing = await ReadWorkingHoursAsync(id, today, connection, transaction, cancellationToken);
            if (hours.Version != PractitionerAvailability.Version(existing))
                throw new BookingException("Working hours changed since you opened this screen. Refresh availability before saving.", true);
            // Weekly History: End previous rules yesterday; only replace today's and future recurring hours.
            using (var command = new SqlCommand("""
                DELETE FROM dbo.AvailabilityRules WHERE PractitionerId = @id AND EffectiveFrom >= @today;
                UPDATE dbo.AvailabilityRules SET EffectiveThrough = DATEADD(day, -1, @today)
                WHERE PractitionerId = @id AND EffectiveFrom < @today AND (EffectiveThrough IS NULL OR EffectiveThrough >= @today);
                """, connection, transaction))
            {
                command.Parameters.Add("@id", SqlDbType.Int).Value = id;
                command.Parameters.Add("@today", SqlDbType.Date).Value = today;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var rule in rules)
            {
                using var command = new SqlCommand("""
                    INSERT dbo.AvailabilityRules(PractitionerId, DayOfWeek, StartTimeLocal, EndTimeLocal, EffectiveFrom)
                    VALUES (@id, @day, @start, @end, @today);
                    """, connection, transaction);
                command.Parameters.Add("@id", SqlDbType.Int).Value = id;
                command.Parameters.Add("@day", SqlDbType.TinyInt).Value = rule.DayOfWeek;
                command.Parameters.Add("@start", SqlDbType.Time).Value = rule.StartTimeLocal;
                command.Parameters.Add("@end", SqlDbType.Time).Value = rule.EndTimeLocal;
                command.Parameters.Add("@today", SqlDbType.Date).Value = today;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else if (timeOff is not null)
        {
            var window = PractitionerAvailability.ValidateTimeOff(timeOff, zone, today);
            using var command = new SqlCommand("""
                SELECT TOP (1) StartAtUtc, EndAtUtc FROM dbo.Appointments
                WHERE PractitionerId = @id AND Status = 'Confirmed' AND StartAtUtc < @end AND EndAtUtc > @start
                ORDER BY StartAtUtc;
                """, connection, transaction);
            command.Parameters.Add("@id", SqlDbType.Int).Value = id;
            command.Parameters.Add("@start", SqlDbType.DateTime2).Value = window.StartAtUtc;
            command.Parameters.Add("@end", SqlDbType.DateTime2).Value = window.EndAtUtc;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), zone);
                    var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), zone);
                    throw new BookingException($"Time off overlaps a confirmed appointment on {start:MMM d, yyyy h:mm tt} to {end:MMM d, yyyy h:mm tt} ({practitioner.TimeZoneId}). Handle that appointment before adding time off.", true);
                }
            }
            // Retry Safety: An identical block is already saved if the first response was lost.
            command.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM dbo.AvailabilityExceptions
                    WHERE PractitionerId = @id AND Kind = 'Blocked' AND StartAtUtc = @start AND EndAtUtc = @end)
                    INSERT dbo.AvailabilityExceptions(PractitionerId, StartAtUtc, EndAtUtc, Kind)
                    VALUES (@id, @start, @end, 'Blocked');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (removeId is { } exceptionId)
        {
            using var command = new SqlCommand("""
                DELETE FROM dbo.AvailabilityExceptions
                WHERE PractitionerId = @id AND AvailabilityExceptionId = @exceptionId AND Kind = 'Blocked';
                """, connection, transaction);
            command.Parameters.Add("@id", SqlDbType.Int).Value = id;
            command.Parameters.Add("@exceptionId", SqlDbType.Int).Value = exceptionId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<WeeklyHours[]> ReadWorkingHoursAsync(int id, DateTime today, SqlConnection connection,
        SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("""
            SELECT DayOfWeek, StartTimeLocal, EndTimeLocal, EffectiveFrom, EffectiveThrough FROM dbo.AvailabilityRules
            WHERE PractitionerId = @id AND (EffectiveThrough IS NULL OR EffectiveThrough >= @today)
            ORDER BY DayOfWeek, StartTimeLocal, EndTimeLocal, EffectiveFrom, EffectiveThrough;
            """, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today;
        var rules = new List<WeeklyHours>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rules.Add(new(reader.GetByte(0), reader.GetTimeSpan(1), reader.GetTimeSpan(2), reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        return rules.ToArray();
    }
}
