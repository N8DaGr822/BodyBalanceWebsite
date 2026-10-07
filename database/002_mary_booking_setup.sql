-- Run after 001_initial_schema.sql against bodybalance. No GO separators are required.
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
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 1)
        THROW 50002, 'Apply schema version 1 first.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 2)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 2 is already applied. No changes made.';
        RETURN;
    END;

    DECLARE @MaryId int = (SELECT PractitionerId FROM dbo.Practitioners WHERE Slug = 'mary');
    IF @MaryId IS NULL
        THROW 50003, 'Mary was not found. No changes made.', 1;
    DECLARE @EffectiveFrom date = CONVERT(date, SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Central Standard Time');
    -- Seed Safety: Review existing configuration rather than silently widening or replacing Mary's hours.
    IF EXISTS (SELECT 1 FROM dbo.AvailabilityRules WHERE PractitionerId = @MaryId
        AND (EffectiveThrough IS NULL OR EffectiveThrough >= @EffectiveFrom))
        THROW 50004, 'Mary already has current or future weekly hours. Review them before applying this migration.', 1;
    IF EXISTS (SELECT 1 FROM dbo.PractitionerServices WHERE PractitionerId = @MaryId AND IsActive = 1)
        THROW 50005, 'Mary already has active services. Review them before applying this migration.', 1;

    ALTER TABLE dbo.Practitioners ADD
        MinimumNoticeHours int NOT NULL CONSTRAINT DF_Practitioners_MinimumNoticeHours DEFAULT 0,
        SlotIntervalMinutes smallint NOT NULL CONSTRAINT DF_Practitioners_SlotIntervalMinutes DEFAULT 30,
        CONSTRAINT CK_Practitioners_MinimumNotice CHECK (MinimumNoticeHours BETWEEN 0 AND 8760),
        CONSTRAINT CK_Practitioners_SlotInterval CHECK (SlotIntervalMinutes BETWEEN 1 AND 1440);
    ALTER TABLE dbo.Services ADD
        Description nvarchar(1000) NULL,
        RegularPrice decimal(10,2) NULL,
        FirstTimeClientPrice decimal(10,2) NULL,
        CONSTRAINT CK_Services_RegularPrice CHECK (RegularPrice IS NULL OR RegularPrice >= Price),
        CONSTRAINT CK_Services_FirstTimeClientPrice CHECK (FirstTimeClientPrice IS NULL OR (FirstTimeClientPrice >= 0 AND FirstTimeClientPrice <= Price));
    ALTER TABLE dbo.Appointments ADD
        BookingRequestId uniqueidentifier NULL,
        IsFirstVisitRequested bit NOT NULL CONSTRAINT DF_Appointments_IsFirstVisitRequested DEFAULT 0;

    -- New Columns: Compile dependent statements after ALTER TABLE, including on a migration rerun.
    EXEC sys.sp_executesql N'
        CREATE UNIQUE INDEX UX_Appointments_BookingRequestId ON dbo.Appointments (BookingRequestId)
            WHERE BookingRequestId IS NOT NULL;
        UPDATE dbo.Practitioners
        SET MinimumNoticeHours = 48, SlotIntervalMinutes = 30,
            Location = COALESCE(NULLIF(LTRIM(RTRIM(Location)), N''''), N''Leavenworth - exact address to be confirmed'')
        WHERE PractitionerId = @MaryId;

        INSERT dbo.Services (Name, DurationMinutes, Price, CurrencyCode, Description, RegularPrice, FirstTimeClientPrice)
        VALUES (N''30-Minute Reiki'', 30, 44.00, ''USD'',
            N''A shorter session designed for relaxation, grounding, and creating space to reconnect with yourself.'', 55.00, NULL);
        DECLARE @ShortServiceId int = CONVERT(int, SCOPE_IDENTITY());
        INSERT dbo.Services (Name, DurationMinutes, Price, CurrencyCode, Description, RegularPrice, FirstTimeClientPrice)
        VALUES (N''60-Minute Reiki'', 60, 111.00, ''USD'',
            N''A full Reiki session allowing more time to slow down, settle in, and explore what may be coming up emotionally, mentally, or energetically.'', NULL, 77.00);
        DECLARE @FullServiceId int = CONVERT(int, SCOPE_IDENTITY());
        INSERT dbo.PractitionerServices (PractitionerId, ServiceId)
        VALUES (@MaryId, @ShortServiceId), (@MaryId, @FullServiceId);',
        N'@MaryId int', @MaryId;

    INSERT dbo.AvailabilityRules (PractitionerId, DayOfWeek, StartTimeLocal, EndTimeLocal, EffectiveFrom)
    SELECT @MaryId, DayOfWeek, '10:00', '18:00', @EffectiveFrom
    FROM (VALUES (2), (3), (4), (5), (6)) AS Days(DayOfWeek);

    INSERT dbo.SchemaVersions (Version) VALUES (2);
    COMMIT TRANSACTION;
    PRINT 'Mary is configured. Apply configure_api_read_access.sql and configure_api_booking_access.sql before deploying the API.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
