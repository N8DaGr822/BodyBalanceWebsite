-- Run as the database administrator after version 14 and the existing review grants.
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 14)
    THROW 50001, 'Apply schema version 14 first.', 1;
IF DATABASE_PRINCIPAL_ID(N'bodybalance-api') IS NULL
    THROW 50002, 'Configure the API database identity first.', 1;
GRANT SELECT ON OBJECT::dbo.Appointments (ScheduleRevision) TO [bodybalance-api];
-- Admin Adjustment: Authenticated practitioner access is required by the date/time update endpoint.
GRANT UPDATE ON OBJECT::dbo.Appointments (StartAtUtc, EndAtUtc, ScheduleRevision) TO [bodybalance-api];
