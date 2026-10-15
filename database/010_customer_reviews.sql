-- Run after version 9, before deploying the customer-review API.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance'
    THROW 50000, 'Run this script against bodybalance.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50001, 'Could not acquire the migration lock.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 9)
        THROW 50002, 'Apply schema version 9 first.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 10)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 10 is already applied. No changes made.';
        RETURN;
    END;

    CREATE TABLE dbo.CustomerReviews
    (
        RequestId uniqueidentifier NOT NULL CONSTRAINT PK_CustomerReviews PRIMARY KEY,
        PractitionerId int NOT NULL CONSTRAINT FK_CustomerReviews_Practitioners REFERENCES dbo.Practitioners(PractitionerId),
        DisplayName nvarchar(150) NOT NULL,
        Message nvarchar(2000) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_CustomerReviews_CreatedAtUtc DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_CustomerReviews_CreatedAtUtc ON dbo.CustomerReviews(CreatedAtUtc DESC, RequestId);
    INSERT dbo.SchemaVersions(Version) VALUES (10);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
