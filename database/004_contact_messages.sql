-- Contact enquiries have their own records and never create or reserve appointments.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 3)
    THROW 50001, 'Apply schema version 3 first.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50002, 'Could not acquire the migration lock.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 4)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 4 is already applied. No changes made.';
        RETURN;
    END;

    CREATE TABLE dbo.ContactMessages
    (
        RequestId uniqueidentifier NOT NULL CONSTRAINT PK_ContactMessages PRIMARY KEY,
        CustomerName nvarchar(150) NOT NULL,
        CustomerEmail nvarchar(254) NOT NULL,
        CustomerPhone nvarchar(30) NULL,
        Message nvarchar(4000) NOT NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_ContactMessages_Created DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_ContactMessages_Created ON dbo.ContactMessages(CreatedAtUtc);
    CREATE TABLE dbo.ContactNotifications
    (
        RequestId uniqueidentifier NOT NULL,
        Channel varchar(5) NOT NULL,
        State varchar(12) NOT NULL CONSTRAINT DF_ContactNotifications_State DEFAULT 'Pending',
        Attempts int NOT NULL CONSTRAINT DF_ContactNotifications_Attempts DEFAULT 0,
        NextAttemptAtUtc datetime2(0) NOT NULL CONSTRAINT DF_ContactNotifications_Next DEFAULT SYSUTCDATETIME(),
        ClaimId uniqueidentifier NULL,
        ClaimedAtUtc datetime2(0) NULL,
        AcceptedAtUtc datetime2(0) NULL,
        ProviderMessageId nvarchar(100) NULL,
        LastError varchar(100) NULL,
        CONSTRAINT PK_ContactNotifications PRIMARY KEY (RequestId, Channel),
        CONSTRAINT FK_ContactNotifications_Messages FOREIGN KEY (RequestId) REFERENCES dbo.ContactMessages(RequestId),
        CONSTRAINT CK_ContactNotifications_Channel CHECK (Channel IN ('Email', 'Sms')),
        CONSTRAINT CK_ContactNotifications_State CHECK (State IN ('Pending', 'Sending', 'Accepted', 'NeedsReview'))
    );
    CREATE INDEX IX_ContactNotifications_Pending ON dbo.ContactNotifications(State, NextAttemptAtUtc);
    INSERT dbo.SchemaVersions(Version) VALUES (4);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
