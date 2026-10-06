using System.Data;
using Azure.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace BodyBalance.Api;

public sealed class ScheduleData(IConfiguration configuration, TokenCredential credential)
{
    // SQL Authentication: Reuse the callback so token refresh does not create a new connection pool per token.
    private readonly Func<SqlAuthenticationParameters, CancellationToken, Task<SqlAuthenticationToken>> tokenCallback = async (_, token) =>
    {
        var accessToken = await credential.GetTokenAsync(
            new TokenRequestContext(["https://database.windows.net/.default"]), token);
        return new SqlAuthenticationToken(accessToken.Token, accessToken.ExpiresOn);
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration["SqlConnectionString"]
            ?? throw new InvalidOperationException("SqlConnectionString is not configured.");
        var connection = new SqlConnection(connectionString) { AccessTokenCallback = tokenCallback };
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<List<Practitioner>> GetPractitionersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            SELECT PractitionerId, DisplayName, TimeZoneId
            FROM dbo.Practitioners WHERE IsActive = 1 ORDER BY PractitionerId;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Practitioner>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<List<TimeWindow>> GetAvailabilityAsync(Practitioner practitioner, DateTime month,
        CancellationToken cancellationToken)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(practitioner.TimeZoneId);
        var start = Availability.ToUtc(month, zone);
        var end = Availability.ToUtc(month.AddMonths(1), zone);
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            SELECT DayOfWeek, StartTimeLocal, EndTimeLocal, EffectiveFrom, EffectiveThrough
            FROM dbo.AvailabilityRules
            WHERE PractitionerId = @id AND EffectiveFrom < @monthEnd
                AND (EffectiveThrough IS NULL OR EffectiveThrough >= @monthStart);

            SELECT StartAtUtc, EndAtUtc, Kind FROM dbo.AvailabilityExceptions
            WHERE PractitionerId = @id AND StartAtUtc < @end AND EndAtUtc > @start;

            SELECT StartAtUtc, EndAtUtc FROM dbo.Appointments
            WHERE PractitionerId = @id AND StartAtUtc < @end AND EndAtUtc > @start
                AND Status IN ('Confirmed', 'Completed', 'NoShow');
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = practitioner.PractitionerId;
        command.Parameters.Add("@monthStart", SqlDbType.Date).Value = month;
        command.Parameters.Add("@monthEnd", SqlDbType.Date).Value = month.AddMonths(1);
        command.Parameters.Add("@start", SqlDbType.DateTime2).Value = start;
        command.Parameters.Add("@end", SqlDbType.DateTime2).Value = end;

        var rules = new List<WeeklyHours>();
        var added = new List<TimeWindow>();
        var blocked = new List<TimeWindow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rules.Add(new(reader.GetByte(0), reader.GetTimeSpan(1), reader.GetTimeSpan(2),
                reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var window = ReadWindow(reader);
            (reader.GetString(2) == "Added" ? added : blocked).Add(window);
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            blocked.Add(ReadWindow(reader));

        return Availability.Calculate(month, zone, rules, added, blocked, DateTime.UtcNow);
    }

    private static TimeWindow ReadWindow(SqlDataReader reader) => new(
        DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
        DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
}
