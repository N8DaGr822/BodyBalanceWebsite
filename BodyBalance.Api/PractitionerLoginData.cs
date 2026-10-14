using System.Data;
using Microsoft.Data.SqlClient;

namespace BodyBalance.Api;

public sealed partial class ScheduleData
{
    public async Task<bool> CreateSignInCodeAsync(string email, byte[] hash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // Send Limits: One code per minute, five per hour; only explicitly enabled accounts can receive codes.
        using var command = new SqlCommand("""
            UPDATE a SET CodeHash = @hash, CodeExpiresAtUtc = DATEADD(minute, 10, SYSUTCDATETIME()),
                FailedAttempts = 0, LastSentAtUtc = SYSUTCDATETIME(),
                WindowSentCount = CASE WHEN WindowStartAtUtc <= DATEADD(hour, -1, SYSUTCDATETIME()) THEN 1 ELSE WindowSentCount + 1 END,
                WindowStartAtUtc = CASE WHEN WindowStartAtUtc <= DATEADD(hour, -1, SYSUTCDATETIME()) THEN SYSUTCDATETIME() ELSE WindowStartAtUtc END
            FROM dbo.PractitionerLoginAccounts a
            INNER JOIN dbo.Practitioners p ON p.PractitionerId = a.PractitionerId
            WHERE a.Email = @email AND a.IsEnabled = 1 AND p.IsActive = 1
                AND (LastSentAtUtc IS NULL OR LastSentAtUtc <= DATEADD(minute, -1, SYSUTCDATETIME()))
                AND (WindowStartAtUtc <= DATEADD(hour, -1, SYSUTCDATETIME()) OR WindowSentCount < 5);
            """, connection);
        command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = email;
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = hash;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> VerifySignInCodeAsync(string email, byte[] codeHash, byte[] sessionHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        int practitionerId;
        bool matches;
        // Single Use: Lock the account until code consumption and session creation commit together.
        using (var command = new SqlCommand("""
            SELECT a.PractitionerId, a.CodeHash FROM dbo.PractitionerLoginAccounts a WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN dbo.Practitioners p ON p.PractitionerId = a.PractitionerId
            WHERE a.Email = @email AND a.IsEnabled = 1 AND p.IsActive = 1
                AND a.CodeExpiresAtUtc > SYSUTCDATETIME() AND a.CodeHash IS NOT NULL AND a.FailedAttempts < 5;
            """, connection, transaction))
        {
            command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = email;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            practitionerId = reader.GetInt32(0);
            matches = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals((byte[])reader[1], codeHash);
        }
        using (var command = new SqlCommand(matches
            ? "UPDATE dbo.PractitionerLoginAccounts SET CodeHash = NULL, CodeExpiresAtUtc = NULL WHERE Email = @email;"
            : "UPDATE dbo.PractitionerLoginAccounts SET FailedAttempts = FailedAttempts + 1 WHERE Email = @email;", connection, transaction))
        {
            command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = email;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (matches)
        {
            using var command = new SqlCommand("""
                INSERT dbo.PractitionerLoginSessions (TokenHash, PractitionerId, ExpiresAtUtc)
                VALUES (@hash, @id, DATEADD(hour, @hours, SYSUTCDATETIME()));
                """, connection, transaction);
            command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = sessionHash;
            command.Parameters.Add("@id", SqlDbType.Int).Value = practitionerId;
            command.Parameters.Add("@hours", SqlDbType.Int).Value = PractitionerLogin.SessionHours;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return matches;
    }

    public async Task<PractitionerSession?> GetLoginSessionAsync(byte[] hash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("""
            SELECT a.PractitionerId, a.Email FROM dbo.PractitionerLoginSessions s
            INNER JOIN dbo.PractitionerLoginAccounts a ON a.PractitionerId = s.PractitionerId
            INNER JOIN dbo.Practitioners p ON p.PractitionerId = a.PractitionerId
            WHERE s.TokenHash = @hash AND s.ExpiresAtUtc > SYSUTCDATETIME() AND s.RevokedAtUtc IS NULL
                AND a.IsEnabled = 1 AND p.IsActive = 1;
            """, connection);
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = hash;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetInt32(0), reader.GetString(1)) : null;
    }

    public async Task RevokeLoginSessionAsync(byte[] hash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = new SqlCommand("UPDATE dbo.PractitionerLoginSessions SET RevokedAtUtc = SYSUTCDATETIME() WHERE TokenHash = @hash AND RevokedAtUtc IS NULL;", connection);
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = hash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
