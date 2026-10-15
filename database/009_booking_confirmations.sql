-- Run after version 8, before deploying the confirmation-email API.
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
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 8)
        THROW 50002, 'Apply schema version 8 first.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 9)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 9 is already applied. No changes made.';
        RETURN;
    END;

    -- Notification Purpose: Preserve request delivery history separately from customer confirmations.
    ALTER TABLE dbo.BookingNotifications ADD Kind varchar(12) NOT NULL
        CONSTRAINT DF_BookingNotifications_Kind DEFAULT 'Request' WITH VALUES;
    ALTER TABLE dbo.BookingNotifications DROP CONSTRAINT PK_BookingNotifications;
    -- Dynamic SQL compiles after the new column exists within this migration batch.
    EXEC(N'ALTER TABLE dbo.BookingNotifications ADD CONSTRAINT PK_BookingNotifications
        PRIMARY KEY (AppointmentId, Channel, Kind);
        ALTER TABLE dbo.BookingNotifications ADD CONSTRAINT CK_BookingNotifications_Kind
        CHECK (Kind = ''Request'' OR (Kind = ''Confirmation'' AND Channel = ''Email''));');

    INSERT dbo.SchemaVersions(Version) VALUES (9);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
