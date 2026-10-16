-- Run after version 13, before deploying appointment date/time editing.
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
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 13)
        THROW 50002, 'Apply schema version 13 first.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 14)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 14 is already applied. No changes made.';
        RETURN;
    END;

    ALTER TABLE dbo.Appointments ADD ScheduleRevision int NOT NULL
        CONSTRAINT DF_Appointments_ScheduleRevision DEFAULT 0 WITH VALUES;
    ALTER TABLE dbo.BookingNotifications ADD Revision int NOT NULL
        CONSTRAINT DF_BookingNotifications_Revision DEFAULT 0 WITH VALUES,
        MessageJson nvarchar(max) NULL;
    ALTER TABLE dbo.BookingNotifications DROP CONSTRAINT PK_BookingNotifications;
    ALTER TABLE dbo.BookingNotifications DROP CONSTRAINT CK_BookingNotifications_Kind;
    -- Dynamic SQL: Compile references to newly added columns after ALTER TABLE completes.
    EXEC(N'ALTER TABLE dbo.BookingNotifications ADD CONSTRAINT PK_BookingNotifications
        PRIMARY KEY (AppointmentId, Channel, Kind, Revision);
        ALTER TABLE dbo.BookingNotifications ADD CONSTRAINT CK_BookingNotifications_Kind
        CHECK ((Kind = ''Request'' AND Revision = 0)
            OR (Kind IN (''Confirmation'', ''Cancellation'') AND Channel = ''Email'' AND Revision = 0)
            OR (Kind = ''Rescheduled'' AND Channel = ''Email'' AND Revision > 0 AND MessageJson IS NOT NULL));');

    INSERT dbo.SchemaVersions(Version) VALUES (14);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
