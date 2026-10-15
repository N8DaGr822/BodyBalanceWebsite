-- Run after 007_thirty_minute_reiki_pricing.sql against bodybalance.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'bodybalance'
    THROW 50000, 'Run this script against the bodybalance database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock
        @Resource = N'BodyBalance.SchemaMigration', @LockMode = 'Exclusive',
        @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0
        THROW 50001, 'Could not acquire the schema migration lock.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 7)
        THROW 50002, 'Apply schema version 7 first.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 8)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 8 is already applied. No changes made.';
        RETURN;
    END;

    DECLARE @MaryId int = (SELECT PractitionerId FROM dbo.Practitioners WHERE Slug = 'mary');
    IF @MaryId IS NULL
        THROW 50003, 'Mary was not found. No changes made.', 1;

    DECLARE @Resource nvarchar(255) = CONCAT(N'BodyBalance.Practitioner.', @MaryId);
    EXEC @LockResult = sys.sp_getapplock
        @Resource = @Resource, @LockMode = 'Exclusive',
        @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0
        THROW 50004, 'Could not acquire the practitioner booking lock.', 1;

    -- Distance Reiki: Use Mary's existing availability and reservation checks for remote sessions.
    IF EXISTS (SELECT 1 FROM dbo.Services s
        JOIN dbo.PractitionerServices ps ON ps.ServiceId = s.ServiceId
        WHERE ps.PractitionerId = @MaryId AND s.Name = N'30-Minute Distance Reiki')
        THROW 50005, 'Mary already has a distance Reiki service. Review configuration before applying this migration.', 1;

    INSERT dbo.Services (Name, DurationMinutes, Price, CurrencyCode, Description, RegularPrice, FirstTimeClientPrice)
    VALUES (N'30-Minute Distance Reiki', 30, 44.00, 'USD',
        N'A Reiki session offered remotely, so you can take time to relax and reconnect from the comfort of your own space.', NULL, NULL);
    DECLARE @ServiceId int = CONVERT(int, SCOPE_IDENTITY());
    INSERT dbo.PractitionerServices (PractitionerId, ServiceId)
    VALUES (@MaryId, @ServiceId);

    INSERT dbo.SchemaVersions (Version) VALUES (8);
    COMMIT TRANSACTION;
    PRINT 'Mary now offers 30-Minute Distance Reiki for $44.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
