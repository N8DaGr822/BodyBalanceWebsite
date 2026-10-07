-- Run after version 2. Private approval credentials and notification routing never appear in public responses.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance'
    THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 2)
    THROW 50001, 'Apply schema version 2 first.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50002, 'Could not acquire the migration lock.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 3)
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Schema version 3 is already applied. No changes made.';
        RETURN;
    END;

    ALTER TABLE dbo.Practitioners ADD NotificationEmail nvarchar(254) NULL, NotificationPhone nvarchar(30) NULL;
    ALTER TABLE dbo.Appointments ADD ReviewTokenHash binary(32) NULL,
        ReviewExpiresAtUtc datetime2(0) NULL, ReviewedAtUtc datetime2(0) NULL;
    CREATE INDEX IX_Appointments_Practitioner_Created ON dbo.Appointments(PractitionerId, CreatedAtUtc);

    -- Notification Outbox: Save both channels in the same transaction as the request.
    CREATE TABLE dbo.BookingNotifications
    (
        AppointmentId bigint NOT NULL,
        Channel varchar(5) NOT NULL,
        State varchar(12) NOT NULL CONSTRAINT DF_BookingNotifications_State DEFAULT 'Pending',
        Attempts int NOT NULL CONSTRAINT DF_BookingNotifications_Attempts DEFAULT 0,
        NextAttemptAtUtc datetime2(0) NOT NULL CONSTRAINT DF_BookingNotifications_Next DEFAULT SYSUTCDATETIME(),
        ClaimId uniqueidentifier NULL,
        ClaimedAtUtc datetime2(0) NULL,
        AcceptedAtUtc datetime2(0) NULL,
        ProviderMessageId nvarchar(100) NULL,
        LastError varchar(100) NULL,
        CONSTRAINT PK_BookingNotifications PRIMARY KEY (AppointmentId, Channel),
        CONSTRAINT FK_BookingNotifications_Appointments FOREIGN KEY (AppointmentId) REFERENCES dbo.Appointments(AppointmentId),
        CONSTRAINT CK_BookingNotifications_Channel CHECK (Channel IN ('Email', 'Sms')),
        CONSTRAINT CK_BookingNotifications_State CHECK (State IN ('Pending', 'Sending', 'Accepted', 'NeedsReview'))
    );
    CREATE INDEX IX_BookingNotifications_Pending ON dbo.BookingNotifications(State, NextAttemptAtUtc);
    INSERT dbo.SchemaVersions(Version) VALUES (3);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
