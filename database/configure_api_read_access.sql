-- Run manually as your Microsoft Entra database administrator AFTER registering
-- a single-tenant Entra application with the unique display name bodybalance-api.
-- Apply schema version 2 first. This grants the API identity access, not your personal login.
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance'
    THROW 50000, 'Run this script against bodybalance.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    IF DATABASE_PRINCIPAL_ID(N'bodybalance-api') IS NULL
        CREATE USER [bodybalance-api] FROM EXTERNAL PROVIDER;

    GRANT SELECT ON OBJECT::dbo.Practitioners
        (PractitionerId, DisplayName, TimeZoneId, IsActive, MinimumNoticeHours, SlotIntervalMinutes, Location) TO [bodybalance-api];
    GRANT SELECT ON OBJECT::dbo.Services
        (ServiceId, Name, DurationMinutes, Price, CurrencyCode, Description, RegularPrice, FirstTimeClientPrice, IsActive) TO [bodybalance-api];
    GRANT SELECT ON OBJECT::dbo.PractitionerServices
        (PractitionerId, ServiceId, IsActive) TO [bodybalance-api];
    GRANT SELECT ON OBJECT::dbo.AvailabilityRules
        (PractitionerId, DayOfWeek, StartTimeLocal, EndTimeLocal, EffectiveFrom, EffectiveThrough) TO [bodybalance-api];
    GRANT SELECT ON OBJECT::dbo.AvailabilityExceptions
        (PractitionerId, StartAtUtc, EndAtUtc, Kind) TO [bodybalance-api];
    GRANT SELECT ON OBJECT::dbo.Appointments
        (PractitionerId, StartAtUtc, EndAtUtc, Status) TO [bodybalance-api];
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT name, type_desc FROM sys.database_principals WHERE name = N'bodybalance-api';
