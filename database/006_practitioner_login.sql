-- Practitioner Login: Explicitly provisioned accounts; no public registration or password storage.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 5)
    THROW 50001, 'Apply schema version 5 first.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50002, 'Could not acquire the migration lock.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 6)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 6 is already applied. No changes made.';
        RETURN;
    END;
    CREATE TABLE dbo.PractitionerLoginAccounts (
        PractitionerId int NOT NULL PRIMARY KEY REFERENCES dbo.Practitioners(PractitionerId),
        Email nvarchar(254) NOT NULL UNIQUE,
        IsEnabled bit NOT NULL DEFAULT 0,
        CodeHash binary(32) NULL,
        CodeExpiresAtUtc datetime2(7) NULL,
        FailedAttempts int NOT NULL DEFAULT 0,
        LastSentAtUtc datetime2(7) NULL,
        WindowStartAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
        WindowSentCount int NOT NULL DEFAULT 0
    );
    CREATE TABLE dbo.PractitionerLoginSessions (
        TokenHash binary(32) NOT NULL PRIMARY KEY,
        PractitionerId int NOT NULL REFERENCES dbo.PractitionerLoginAccounts(PractitionerId),
        CreatedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
        ExpiresAtUtc datetime2(7) NOT NULL,
        RevokedAtUtc datetime2(7) NULL
    );
    CREATE INDEX IX_PractitionerLoginSessions_Expiry ON dbo.PractitionerLoginSessions(ExpiresAtUtc);
    INSERT dbo.SchemaVersions(Version) VALUES (6);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
