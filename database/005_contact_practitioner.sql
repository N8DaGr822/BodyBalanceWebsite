-- Contact Routing: Preserve legacy shared enquiries; new messages identify their selected practitioner.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 4)
    THROW 50001, 'Apply schema version 4 first.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50002, 'Could not acquire the migration lock.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 5)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 5 is already applied. No changes made.';
        RETURN;
    END;
    ALTER TABLE dbo.ContactMessages ADD PractitionerId int NULL
        CONSTRAINT FK_ContactMessages_Practitioners REFERENCES dbo.Practitioners(PractitionerId);
    INSERT dbo.SchemaVersions(Version) VALUES (5);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
