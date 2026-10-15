SET XACT_ABORT ON;
IF DB_NAME() <> N'bodybalance' THROW 50000, 'Run this script against bodybalance.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE Version = 12)
    THROW 50001, 'Apply schema version 12 first.', 1;
IF DATABASE_PRINCIPAL_ID(N'bodybalance-api') IS NULL
    THROW 50002, 'Configure the API database identity first.', 1;
GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.MailConversations TO [bodybalance-api];
GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.MailMessages TO [bodybalance-api];
