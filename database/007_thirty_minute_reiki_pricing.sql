-- Run after 006_practitioner_login.sql against bodybalance.
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
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 6)
        THROW 50002, 'Apply schema version 6 first.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 7)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 7 is already applied. No changes made.';
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

    -- Session Pricing: Match the full session's standard/first-visit pricing without changing appointment snapshots.
    UPDATE s
    SET Price = 55.00, RegularPrice = NULL, FirstTimeClientPrice = 44.00
    FROM dbo.Services s
    JOIN dbo.PractitionerServices ps ON ps.ServiceId = s.ServiceId
    WHERE ps.PractitionerId = @MaryId AND ps.IsActive = 1
        AND s.Name = N'30-Minute Reiki' AND s.DurationMinutes = 30 AND s.IsActive = 1
        AND s.CurrencyCode = 'USD' AND s.Price = 44.00
        AND s.RegularPrice = 55.00 AND s.FirstTimeClientPrice IS NULL
        AND NOT EXISTS (SELECT 1 FROM dbo.PractitionerServices other
            WHERE other.ServiceId = s.ServiceId AND other.PractitionerId <> @MaryId);
    IF @@ROWCOUNT <> 1
        THROW 50005, 'Expected one original Mary-only 30-Minute Reiki service. Review configuration before applying this migration.', 1;

    INSERT dbo.SchemaVersions (Version) VALUES (7);
    COMMIT TRANSACTION;
    PRINT '30-Minute Reiki is $55, with a $44 first-time client price.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
