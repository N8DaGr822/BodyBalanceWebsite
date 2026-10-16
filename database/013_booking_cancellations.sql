-- Run after version 12, before deploying the cancellation-email API.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance'
    THROW 50000, 'Run this script against bodybalance.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50001, 'Could not acquire the migration lock.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 12)
        THROW 50002, 'Apply schema version 12 first.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 13)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 13 is already applied. No changes made.';
        RETURN;
    END;

    ALTER TABLE dbo.BookingNotifications DROP CONSTRAINT CK_BookingNotifications_Kind;
    ALTER TABLE dbo.BookingNotifications ADD CONSTRAINT CK_BookingNotifications_Kind
        CHECK (Kind = 'Request' OR (Kind IN ('Confirmation', 'Cancellation') AND Channel = 'Email'));

    INSERT dbo.SchemaVersions(Version) VALUES (13);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
