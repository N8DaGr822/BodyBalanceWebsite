using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed partial class ScheduleData
{
    public async Task<List<MailConversation>> GetConversationsAsync(int offset, CancellationToken cancellationToken, bool trashed = false)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // Deployment Gap: Import contact messages saved by the previous API between migration and deployment.
        using (var import = new SqlCommand("""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            INSERT dbo.MailConversations (ConversationId, CustomerName, CustomerEmail, Subject, UpdatedAtUtc)
                SELECT c.RequestId, c.CustomerName, c.CustomerEmail, N'Website inquiry', c.CreatedAtUtc
                FROM dbo.ContactMessages c WHERE NOT EXISTS
                    (SELECT 1 FROM dbo.MailConversations m WITH (UPDLOCK, HOLDLOCK) WHERE m.ConversationId = c.RequestId);
            INSERT dbo.MailMessages (MessageId, ConversationId, Direction, Sender, Body, CreatedAtUtc, State)
                SELECT c.RequestId, c.RequestId, 'Incoming', c.CustomerEmail, c.Message, c.CreatedAtUtc, 'Received'
                FROM dbo.ContactMessages c WHERE NOT EXISTS
                    (SELECT 1 FROM dbo.MailMessages m WITH (UPDLOCK, HOLDLOCK) WHERE m.MessageId = c.RequestId);
            COMMIT;
            """, connection)) await import.ExecuteNonQueryAsync(cancellationToken);
        using var command = new SqlCommand("""
            SELECT ConversationId, CustomerName, CustomerEmail, Subject, UpdatedAtUtc, IsTrashed
            FROM dbo.MailConversations WHERE IsTrashed = @trashed
            ORDER BY UpdatedAtUtc DESC, ConversationId OFFSET @offset ROWS FETCH NEXT 50 ROWS ONLY;
            """, connection);
        command.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
        command.Parameters.Add("@trashed", SqlDbType.Bit).Value = trashed;
        var result = new List<MailConversation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadConversation(reader));
        return result;
    }

    public async Task<MailConversation?> GetConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("SELECT ConversationId, CustomerName, CustomerEmail, Subject, UpdatedAtUtc, IsTrashed FROM dbo.MailConversations WHERE ConversationId = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    private static MailConversation ReadConversation(SqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc), reader.GetBoolean(5));

    public async Task<bool> SetConversationTrashAsync(Guid id, bool trashed, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // Shared Trash: Retain messages, attachments, and replay identifiers so restoration is lossless.
        using var command = new SqlCommand("UPDATE dbo.MailConversations SET IsTrashed = @trashed WHERE ConversationId = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        command.Parameters.Add("@trashed", SqlDbType.Bit).Value = trashed;
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<List<MailEntry>> GetMailEntriesAsync(Guid id, int offset, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            SELECT MessageId, Direction, Sender, Body, CreatedAtUtc, State, ProviderId,
                CAST(CASE WHEN RawEmail IS NULL THEN 0 ELSE 1 END AS bit), Actor
            FROM dbo.MailMessages WHERE ConversationId = @id
            ORDER BY CreatedAtUtc DESC, MessageId OFFSET @offset ROWS FETCH NEXT 25 ROWS ONLY;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        command.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
        var result = new List<MailEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        return result;
    }

    public async Task SaveIncomingMailAsync(IncomingMail mail, byte[] raw, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        // Inbound Replay: A content hash deduplicates webhook retries, including messages without a Message-ID.
        using var command = new SqlCommand("""
            DECLARE @lock int;
            EXEC @lock = sys.sp_getapplock @Resource=N'BodyBalance.IncomingMail', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
            IF @lock < 0 THROW 50001, 'Inbox busy.', 1;
            IF NOT EXISTS (SELECT 1 FROM dbo.MailMessages WHERE ContentHash = @hash)
            BEGIN
                IF @conversation IS NULL
                    SELECT TOP(1) @conversation=c.ConversationId FROM dbo.MailMessages m
                    JOIN dbo.MailConversations c ON c.ConversationId=m.ConversationId
                    WHERE c.CustomerEmail=@email AND c.CustomerEmail<>''
                        AND m.InternetMessageId IN (SELECT value FROM OPENJSON(@references))
                    ORDER BY m.CreatedAtUtc DESC;
                -- A subject token alone must never attach a different sender to an existing conversation.
                IF @conversation IS NULL OR NOT EXISTS
                    (SELECT 1 FROM dbo.MailConversations WHERE ConversationId = @conversation AND CustomerEmail = @email AND CustomerEmail <> '')
                BEGIN
                    SET @conversation = NEWID();
                    INSERT dbo.MailConversations (ConversationId, CustomerName, CustomerEmail, Subject)
                        VALUES (@conversation, @name, @email, @subject);
                END;
                INSERT dbo.MailMessages (MessageId, ConversationId, Direction, Sender, Body, State, ContentHash, RawEmail, InternetMessageId)
                    VALUES (NEWID(), @conversation, 'Incoming', @sender, @body, 'Received', @hash, @raw, @internetId);
                UPDATE dbo.MailConversations SET UpdatedAtUtc = SYSUTCDATETIME() WHERE ConversationId = @conversation;
            END;
            """, connection, transaction);
        command.Parameters.Add("@conversation", SqlDbType.UniqueIdentifier).Value = (object?)mail.ConversationId ?? DBNull.Value;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 150).Value = mail.Name;
        command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = mail.Email;
        command.Parameters.Add("@sender", SqlDbType.NVarChar, 254).Value = mail.From is { Length: <= 254 } ? mail.From : mail.Email;
        command.Parameters.Add("@subject", SqlDbType.NVarChar, 300).Value = mail.Subject;
        command.Parameters.Add("@body", SqlDbType.NVarChar, -1).Value = mail.Body;
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = SHA256.HashData(raw);
        command.Parameters.Add("@raw", SqlDbType.VarBinary, -1).Value = raw;
        command.Parameters.Add("@internetId", SqlDbType.NVarChar, 998).Value = (object?)mail.InternetMessageId ?? DBNull.Value;
        command.Parameters.Add("@references", SqlDbType.NVarChar, -1).Value = System.Text.Json.JsonSerializer.Serialize(mail.References);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(bool Claimed, string State, string? InReplyTo)> ClaimMailReplyAsync(Guid id, MailReply reply, string sender, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var command = new SqlCommand("""
            DECLARE @lock int;
            EXEC @lock = sys.sp_getapplock @Resource=N'BodyBalance.MailReplies', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
            IF @lock < 0 THROW 50001, 'Inbox busy.', 1;
            IF EXISTS (SELECT 1 FROM dbo.MailMessages WHERE MessageId = @messageId)
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.MailMessages WHERE MessageId=@messageId AND ConversationId=@id AND Direction='Outgoing' AND Body=@body)
                    THROW 50002, 'Reply reference already used.', 1;
                SELECT CAST(0 AS bit), State, CAST(NULL AS nvarchar(998)) FROM dbo.MailMessages WHERE MessageId=@messageId;
            END
            ELSE IF EXISTS (SELECT 1 FROM dbo.MailMessages WHERE ConversationId=@id AND State IN ('Sending','NeedsReview'))
                SELECT CAST(0 AS bit), 'NeedsReview', CAST(NULL AS nvarchar(998));
            ELSE
            BEGIN
                -- Shared Trash: Serialize with trash updates before claiming a new reply.
                IF NOT EXISTS (SELECT 1 FROM dbo.MailConversations WITH (UPDLOCK, HOLDLOCK) WHERE ConversationId=@id AND IsTrashed=0)
                    THROW 50003, 'Restore the conversation before replying.', 1;
                INSERT dbo.MailMessages (MessageId, ConversationId, Direction, Sender, Body, State, Actor)
                    VALUES (@messageId, @id, 'Outgoing', @sender, @body, 'Sending', @actor);
                UPDATE dbo.MailConversations SET UpdatedAtUtc=SYSUTCDATETIME() WHERE ConversationId=@id;
                SELECT CAST(1 AS bit), 'Sending', (SELECT TOP(1) InternetMessageId FROM dbo.MailMessages
                    WHERE ConversationId=@id AND Direction='Incoming' AND InternetMessageId IS NOT NULL ORDER BY CreatedAtUtc DESC);
            END;
            """, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        command.Parameters.Add("@messageId", SqlDbType.UniqueIdentifier).Value = reply.MessageId;
        command.Parameters.Add("@body", SqlDbType.NVarChar, -1).Value = reply.Body!;
        command.Parameters.Add("@sender", SqlDbType.NVarChar, 254).Value = sender;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 254).Value = actor[..Math.Min(actor.Length, 254)];
        (bool, string, string?) result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            result = (reader.GetBoolean(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task FinishMailReplyAsync(Guid messageId, NotificationResult result, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("UPDATE dbo.MailMessages SET State=@state, ProviderId=@provider WHERE MessageId=@id AND State='Sending';", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = messageId;
        command.Parameters.Add("@state", SqlDbType.VarChar, 12).Value = result.State == "Accepted" ? "Accepted" : "NeedsReview";
        command.Parameters.Add("@provider", SqlDbType.NVarChar, 100).Value = (object?)result.ProviderId ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<byte[]?> GetOriginalMailAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("SELECT RawEmail FROM dbo.MailMessages WHERE MessageId=@id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }
}
