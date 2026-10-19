-- Apply before deploying inbox Trash support. No conversations or messages are removed.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50001, 'Could not acquire the migration lock.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 14)
        THROW 50002, 'Apply schema version 14 first.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 15)
    BEGIN
        ALTER TABLE dbo.MailConversations ADD IsTrashed bit NOT NULL
            CONSTRAINT DF_MailConversations_IsTrashed DEFAULT 0;
        INSERT dbo.SchemaVersions(Version) VALUES (15);
    END;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
