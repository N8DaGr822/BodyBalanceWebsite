using System.Data;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed partial class ScheduleData
{
    public async Task<List<CustomerReviewDetails>> GetCustomerReviewsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            SELECT TOP (100) r.RequestId, r.DisplayName, r.Message, p.DisplayName
            FROM dbo.CustomerReviews r JOIN dbo.Practitioners p ON p.PractitionerId = r.PractitionerId
            ORDER BY r.CreatedAtUtc DESC, r.RequestId;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CustomerReviewDetails>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public async Task<Guid> SaveCustomerReviewAsync(CustomerReviewRequest request, CancellationToken cancellationToken)
    {
        request = CustomerReview.Validate(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        using (var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'BodyBalance.CustomerReviews', @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @result;
            """, connection, transaction))
        {
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
                throw new BookingException("Reviews are busy right now. Please retry.", true);
        }
        // Retry Safety: Reusing a submission reference cannot publish the same review twice.
        using (var command = new SqlCommand("SELECT DisplayName, Message, PractitionerId FROM dbo.CustomerReviews WHERE RequestId = @id;", connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = request.RequestId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != request.Name || reader.GetString(1) != request.Message || reader.GetInt32(2) != request.PractitionerId)
                    throw new BookingException("This review reference was already used. Refresh before submitting another review.", true);
                return request.RequestId;
            }
        }
        if (!(await GetPractitionersAsync(connection, transaction, cancellationToken)).Any(p => p.PractitionerId == request.PractitionerId))
            throw new BookingException("Choose an available practitioner.");
        var limit = int.TryParse(configuration["CustomerReviewsPerHour"], out var configuredLimit) && configuredLimit > 0 ? configuredLimit : 20;
        using (var command = new SqlCommand("SELECT COUNT(*) FROM dbo.CustomerReviews WHERE CreatedAtUtc >= DATEADD(hour, -1, SYSUTCDATETIME());", connection, transaction))
        {
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= limit)
                throw new BookingException("Reviews are temporarily at capacity. Please try again later.");
        }
        using (var command = new SqlCommand("""
            INSERT dbo.CustomerReviews (RequestId, DisplayName, Message, PractitionerId)
            VALUES (@id, @name, @message, @practitionerId);
            """, connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = request.RequestId;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 150).Value = request.Name!;
            command.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = request.Message!;
            command.Parameters.Add("@practitionerId", SqlDbType.Int).Value = request.PractitionerId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return request.RequestId;
    }
}
