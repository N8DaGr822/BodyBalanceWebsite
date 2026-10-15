-- Run as the database administrator before deploying practitioner availability editing.
-- Uses the existing version 1 tables; no schema migration is required.
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
IF DATABASE_PRINCIPAL_ID(N'bodybalance-api') IS NULL
    THROW 50001, 'Configure the API database identity first.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    GRANT INSERT, DELETE ON OBJECT::dbo.AvailabilityRules TO [bodybalance-api];
    GRANT UPDATE ON OBJECT::dbo.AvailabilityRules (EffectiveThrough) TO [bodybalance-api];
    GRANT SELECT ON OBJECT::dbo.AvailabilityExceptions (AvailabilityExceptionId) TO [bodybalance-api];
    GRANT INSERT, DELETE ON OBJECT::dbo.AvailabilityExceptions TO [bodybalance-api];
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
