-- Run against bodybalance in Azure SQL Query editor. No GO separators are required.
-- Schema Setup: Commit the schema and seed data together, or roll back on failure.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'bodybalance'
    THROW 50000, 'Run this script against the bodybalance database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock
        @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive',
        @LockOwner = 'Transaction',
        @LockTimeout = 10000;

    IF @LockResult < 0
        THROW 50001, 'Could not acquire the schema migration lock. Try again later.', 1;

    IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SchemaVersions
        (
            Version int NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
            AppliedAtUtc datetime2(0) NOT NULL
                CONSTRAINT DF_SchemaVersions_AppliedAtUtc DEFAULT SYSUTCDATETIME()
        );
    END;

    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 1)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 1 is already applied. No changes made.';
        RETURN;
    END;

    CREATE TABLE dbo.Practitioners
    (
        PractitionerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Practitioners PRIMARY KEY,
        Slug varchar(50) NOT NULL CONSTRAINT UQ_Practitioners_Slug UNIQUE,
        DisplayName nvarchar(100) NOT NULL,
        -- Time Zone: Windows identifier includes Central daylight-saving rules, not a fixed UTC offset.
        TimeZoneId nvarchar(100) NOT NULL,
        Location nvarchar(500) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Practitioners_IsActive DEFAULT 1
    );

    CREATE TABLE dbo.Services
    (
        ServiceId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Services PRIMARY KEY,
        Name nvarchar(150) NOT NULL,
        DurationMinutes smallint NOT NULL,
        Price decimal(10,2) NOT NULL,
        CurrencyCode char(3) NOT NULL CONSTRAINT DF_Services_CurrencyCode DEFAULT 'USD',
        IsActive bit NOT NULL CONSTRAINT DF_Services_IsActive DEFAULT 1,
        CONSTRAINT CK_Services_Duration CHECK (DurationMinutes > 0),
        CONSTRAINT CK_Services_Price CHECK (Price >= 0)
    );

    CREATE TABLE dbo.PractitionerServices
    (
        PractitionerId int NOT NULL,
        ServiceId int NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_PractitionerServices_IsActive DEFAULT 1,
        CONSTRAINT PK_PractitionerServices PRIMARY KEY (PractitionerId, ServiceId),
        CONSTRAINT FK_PractitionerServices_Practitioners FOREIGN KEY (PractitionerId)
            REFERENCES dbo.Practitioners (PractitionerId),
        CONSTRAINT FK_PractitionerServices_Services FOREIGN KEY (ServiceId)
            REFERENCES dbo.Services (ServiceId)
    );

    CREATE TABLE dbo.AvailabilityRules
    (
        AvailabilityRuleId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AvailabilityRules PRIMARY KEY,
        PractitionerId int NOT NULL,
        -- Weekly Hours: Sunday = 0 through Saturday = 6, independent of SQL DATEFIRST.
        DayOfWeek tinyint NOT NULL,
        StartTimeLocal time(0) NOT NULL,
        EndTimeLocal time(0) NOT NULL,
        EffectiveFrom date NOT NULL,
        EffectiveThrough date NULL,
        CONSTRAINT FK_AvailabilityRules_Practitioners FOREIGN KEY (PractitionerId)
            REFERENCES dbo.Practitioners (PractitionerId),
        CONSTRAINT CK_AvailabilityRules_Day CHECK (DayOfWeek BETWEEN 0 AND 6),
        CONSTRAINT CK_AvailabilityRules_Time CHECK (StartTimeLocal < EndTimeLocal),
        CONSTRAINT CK_AvailabilityRules_Dates CHECK (EffectiveThrough IS NULL OR EffectiveThrough >= EffectiveFrom)
    );

    CREATE INDEX IX_AvailabilityRules_Practitioner_Day
        ON dbo.AvailabilityRules (PractitionerId, DayOfWeek, EffectiveFrom);

    CREATE TABLE dbo.AvailabilityExceptions
    (
        AvailabilityExceptionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AvailabilityExceptions PRIMARY KEY,
        PractitionerId int NOT NULL,
        StartAtUtc datetime2(0) NOT NULL,
        EndAtUtc datetime2(0) NOT NULL,
        -- Availability: Union Added intervals with weekly hours, then subtract Blocked intervals.
        Kind varchar(10) NOT NULL,
        Reason nvarchar(250) NULL,
        CONSTRAINT FK_AvailabilityExceptions_Practitioners FOREIGN KEY (PractitionerId)
            REFERENCES dbo.Practitioners (PractitionerId),
        CONSTRAINT CK_AvailabilityExceptions_Time CHECK (StartAtUtc < EndAtUtc),
        CONSTRAINT CK_AvailabilityExceptions_Kind CHECK (Kind IN ('Added', 'Blocked'))
    );

    CREATE INDEX IX_AvailabilityExceptions_Practitioner_Start
        ON dbo.AvailabilityExceptions (PractitionerId, StartAtUtc) INCLUDE (EndAtUtc, Kind);

    CREATE TABLE dbo.Appointments
    (
        AppointmentId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Appointments PRIMARY KEY,
        PractitionerId int NOT NULL,
        ServiceId int NOT NULL,
        CustomerName nvarchar(150) NOT NULL,
        CustomerEmail nvarchar(254) NOT NULL,
        CustomerPhone nvarchar(30) NULL,
        StartAtUtc datetime2(0) NOT NULL,
        EndAtUtc datetime2(0) NOT NULL,
        -- Booking History: Snapshot these values so later practitioner/service edits do not alter the booking.
        ServiceName nvarchar(150) NOT NULL,
        Price decimal(10,2) NOT NULL,
        CurrencyCode char(3) NOT NULL,
        Location nvarchar(500) NOT NULL,
        TimeZoneId nvarchar(100) NOT NULL,
        -- Requests: A requested time does not reserve availability until confirmation.
        Status varchar(12) NOT NULL CONSTRAINT DF_Appointments_Status DEFAULT 'Requested',
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_Appointments_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_Appointments_PractitionerServices FOREIGN KEY (PractitionerId, ServiceId)
            REFERENCES dbo.PractitionerServices (PractitionerId, ServiceId),
        CONSTRAINT CK_Appointments_Time CHECK (StartAtUtc < EndAtUtc),
        CONSTRAINT CK_Appointments_Price CHECK (Price >= 0),
        CONSTRAINT CK_Appointments_Status CHECK (Status IN ('Requested', 'Confirmed', 'Cancelled', 'Completed', 'NoShow')),
        CONSTRAINT CK_Appointments_CustomerName CHECK (LEN(LTRIM(RTRIM(CustomerName))) > 0),
        CONSTRAINT CK_Appointments_CustomerEmail CHECK (LEN(LTRIM(RTRIM(CustomerEmail))) > 0),
        CONSTRAINT CK_Appointments_Location CHECK (LEN(LTRIM(RTRIM(Location))) > 0)
    );

    -- Booking Lookup: Supports per-practitioner range checks; this index alone does NOT prevent overlaps.
    CREATE INDEX IX_Appointments_Practitioner_Start
        ON dbo.Appointments (PractitionerId, StartAtUtc) INCLUDE (EndAtUtc, Status);

    INSERT dbo.Practitioners (Slug, DisplayName, TimeZoneId)
    VALUES ('mary', N'Mary', N'Central Standard Time'),
           ('leslie', N'Leslie', N'Central Standard Time');

    -- Availability Setup: Do not seed unconfirmed locations, services, working hours, or appointments.
    INSERT dbo.SchemaVersions (Version) VALUES (1);

    COMMIT TRANSACTION;
    SELECT PractitionerId, DisplayName, TimeZoneId, Location FROM dbo.Practitioners;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
