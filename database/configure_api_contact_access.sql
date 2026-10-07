-- Apply after version 4 as the database administrator.
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 4)
    THROW 50001, 'Apply schema version 4 first.', 1;
IF DATABASE_PRINCIPAL_ID(N'bodybalance-api') IS NULL
    THROW 50002, 'Configure the API database identity first.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    GRANT SELECT, INSERT ON OBJECT::dbo.ContactMessages TO [bodybalance-api];
    GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.ContactNotifications TO [bodybalance-api];
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
