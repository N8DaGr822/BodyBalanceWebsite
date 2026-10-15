-- Apply before deploying the shared inbox. Existing contact messages are preserved and imported.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'BodyBalance.SchemaMigration',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 50001, 'Could not acquire the migration lock.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 11)
        THROW 50002, 'Apply schema version 11 first.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 12)
    BEGIN
        CREATE TABLE dbo.MailConversations
        (
            ConversationId uniqueidentifier NOT NULL PRIMARY KEY,
            CustomerName nvarchar(150) NOT NULL,
            CustomerEmail nvarchar(254) NOT NULL,
            Subject nvarchar(300) NOT NULL,
            UpdatedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME()
        );
        CREATE INDEX IX_MailConversations_Updated ON dbo.MailConversations(UpdatedAtUtc DESC, ConversationId);
        CREATE TABLE dbo.MailMessages
        (
            MessageId uniqueidentifier NOT NULL PRIMARY KEY,
            ConversationId uniqueidentifier NOT NULL REFERENCES dbo.MailConversations(ConversationId),
            Direction varchar(8) NOT NULL CHECK (Direction IN ('Incoming', 'Outgoing')),
            Sender nvarchar(254) NOT NULL,
            Body nvarchar(max) NOT NULL,
            CreatedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            State varchar(12) NOT NULL CHECK (State IN ('Received', 'Sending', 'Accepted', 'NeedsReview', 'NotSent')),
            ProviderId nvarchar(100) NULL,
            InternetMessageId nvarchar(998) NULL,
            ContentHash binary(32) NULL,
            RawEmail varbinary(max) NULL,
            Actor nvarchar(254) NULL
        );
        CREATE INDEX IX_MailMessages_Conversation ON dbo.MailMessages(ConversationId, CreatedAtUtc, MessageId);
        CREATE UNIQUE INDEX UX_MailMessages_Hash ON dbo.MailMessages(ContentHash) WHERE ContentHash IS NOT NULL;
        INSERT dbo.MailConversations (ConversationId, CustomerName, CustomerEmail, Subject, UpdatedAtUtc)
            SELECT RequestId, CustomerName, CustomerEmail, N'Website inquiry', CreatedAtUtc FROM dbo.ContactMessages;
        INSERT dbo.MailMessages (MessageId, ConversationId, Direction, Sender, Body, CreatedAtUtc, State)
            SELECT RequestId, RequestId, 'Incoming', CustomerEmail, Message, CreatedAtUtc, 'Received' FROM dbo.ContactMessages;
        INSERT dbo.SchemaVersions(Version) VALUES (12);
    END;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
