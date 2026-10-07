-- Run as the Entra database administrator after version 2 and configure_api_read_access.sql.
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance'
    THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 2)
    THROW 50001, 'Apply schema version 2 first.', 1;
IF DATABASE_PRINCIPAL_ID(N'bodybalance-api') IS NULL
    THROW 50002, 'Run configure_api_read_access.sql first.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    -- Request Submission: Customer fields stay on the backend for validation and retry detection.
    GRANT SELECT ON OBJECT::dbo.Appointments
        (BookingRequestId, CustomerName, CustomerEmail, CustomerPhone, ServiceId,
         ServiceName, Location, TimeZoneId, IsFirstVisitRequested) TO [bodybalance-api];
    GRANT INSERT ON OBJECT::dbo.Appointments TO [bodybalance-api];
    -- Approval remains an administrator operation; the public API cannot update or delete appointments.
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
