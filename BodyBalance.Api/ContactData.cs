using System.Data;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed partial class ScheduleData
{
    public async Task<ContactReceipt> SaveContactAsync(ContactRequest request, CancellationToken cancellationToken)
    {
        request = ContactMessage.Validate(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        using (var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'BodyBalance.ContactMessages', @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @result;
            """, connection, transaction))
        {
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
                throw new BookingException("Messages are busy right now. Please retry.", true);
        }
        // Retry Safety: A lost response cannot create another message or another pair of notifications.
        using (var command = new SqlCommand("""
            SELECT CustomerName, CustomerEmail, CustomerPhone, Message FROM dbo.ContactMessages WHERE RequestId = @id;
            """, connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = request.RequestId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != request.Name || reader.GetString(1) != request.Email
                    || (reader.IsDBNull(2) ? null : reader.GetString(2)) != request.Phone || reader.GetString(3) != request.Message)
                    throw new BookingException("This message reference was already used. Refresh before sending another message.", true);
                return new(request.RequestId);
            }
        }
        var limit = int.TryParse(configuration["ContactMessagesPerHour"], out var configuredLimit) && configuredLimit > 0 ? configuredLimit : 20;
        using (var command = new SqlCommand("SELECT COUNT(*) FROM dbo.ContactMessages WHERE CreatedAtUtc >= DATEADD(hour, -1, SYSUTCDATETIME());", connection, transaction))
        {
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= limit)
                throw new BookingException("Online messages are temporarily at capacity. Please try again later.");
        }
        using (var command = new SqlCommand("""
            INSERT dbo.ContactMessages (RequestId, CustomerName, CustomerEmail, CustomerPhone, Message)
            VALUES (@id, @name, @email, @phone, @message);
            INSERT dbo.ContactNotifications (RequestId, Channel) VALUES (@id, 'Email'), (@id, 'Sms');
            """, connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = request.RequestId;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 150).Value = request.Name!;
            command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = request.Email!;
            command.Parameters.Add("@phone", SqlDbType.NVarChar, 30).Value = (object?)request.Phone ?? DBNull.Value;
            command.Parameters.Add("@message", SqlDbType.NVarChar, 4000).Value = request.Message!;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(request.RequestId);
    }

    public async Task<ContactNotification?> ClaimContactNotificationAsync(Guid? requestId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            ;WITH candidate AS
            (
                SELECT TOP (1) * FROM dbo.ContactNotifications WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE State = 'Pending' AND Attempts < 5 AND NextAttemptAtUtc <= SYSUTCDATETIME()
                    AND (@id IS NULL OR RequestId = @id)
                ORDER BY NextAttemptAtUtc, RequestId, Channel
            )
            UPDATE candidate SET State = 'Sending', ClaimId = NEWID(), ClaimedAtUtc = SYSUTCDATETIME(), Attempts = Attempts + 1
                OUTPUT inserted.RequestId, inserted.Channel, inserted.ClaimId, inserted.Attempts;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = (object?)requestId ?? DBNull.Value;
        Guid id;
        string channel;
        Guid claimId;
        int attempts;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            id = reader.GetGuid(0);
            channel = reader.GetString(1);
            claimId = reader.GetGuid(2);
            attempts = reader.GetInt32(3);
        }
        using var details = new SqlCommand("SELECT CustomerName, CustomerEmail, CustomerPhone, Message FROM dbo.ContactMessages WHERE RequestId = @id;", connection);
        details.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        await using var result = await details.ExecuteReaderAsync(cancellationToken);
        if (!await result.ReadAsync(cancellationToken)) return null;
        return new(id, channel, claimId, attempts, new(id, result.GetString(0), result.GetString(1),
            result.IsDBNull(2) ? null : result.GetString(2), result.GetString(3)));
    }

    public async Task FinishContactNotificationAsync(ContactNotification notification, NotificationResult result, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            UPDATE dbo.ContactNotifications SET State = @state, ProviderMessageId = @providerId, LastError = @error,
                AcceptedAtUtc = CASE WHEN @state = 'Accepted' THEN SYSUTCDATETIME() ELSE NULL END,
                NextAttemptAtUtc = DATEADD(minute, 15, SYSUTCDATETIME())
            WHERE RequestId = @id AND Channel = @channel AND State = 'Sending' AND ClaimId = @claimId;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = notification.RequestId;
        command.Parameters.Add("@channel", SqlDbType.VarChar, 5).Value = notification.Channel;
        command.Parameters.Add("@claimId", SqlDbType.UniqueIdentifier).Value = notification.ClaimId;
        command.Parameters.Add("@state", SqlDbType.VarChar, 12).Value = result.State == "Pending" && notification.Attempts >= 5 ? "NeedsReview" : result.State;
        command.Parameters.Add("@providerId", SqlDbType.NVarChar, 100).Value = (object?)result.ProviderId ?? DBNull.Value;
        command.Parameters.Add("@error", SqlDbType.VarChar, 100).Value = (object?)result.Error ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
