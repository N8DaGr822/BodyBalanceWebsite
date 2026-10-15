-- Apply after version 10, before deploying review removal.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50001, 'Could not acquire the migration lock.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 10)
        THROW 50002, 'Apply schema version 10 first.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 11)
    BEGIN
        ALTER TABLE dbo.CustomerReviews ADD IsRemoved bit NOT NULL
            CONSTRAINT DF_CustomerReviews_IsRemoved DEFAULT 0 WITH VALUES;
        INSERT dbo.SchemaVersions(Version) VALUES (11);
    END;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
