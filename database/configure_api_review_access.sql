-- Run as the database administrator after version 3 and the existing read/booking grants.
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 3)
    THROW 50001, 'Apply schema version 3 first.', 1;
IF DATABASE_PRINCIPAL_ID(N'bodybalance-api') IS NULL
    THROW 50002, 'Configure the API database identity first.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    GRANT SELECT ON OBJECT::dbo.Practitioners (NotificationEmail, NotificationPhone) TO [bodybalance-api];
    GRANT SELECT ON OBJECT::dbo.Appointments
        (AppointmentId, Price, CurrencyCode, CreatedAtUtc, ReviewTokenHash, ReviewExpiresAtUtc, ReviewedAtUtc) TO [bodybalance-api];
    -- Approval Boundary: Only the private token-validated endpoint can change these booking fields.
    GRANT UPDATE ON OBJECT::dbo.Appointments (Status, Price, Location, ReviewedAtUtc) TO [bodybalance-api];
    GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.BookingNotifications TO [bodybalance-api];
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
