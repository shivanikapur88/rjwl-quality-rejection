/* ============================================================================
   16_upgrade_master_login_known_clients.sql  -  master database (QualityMasterDB). Idempotent.

   Login throttling exempts a login that has already signed in successfully from the same client address (so an attacker
   guessing a user's password elsewhere cannot lock that user out of the tablet they use, and one faulty tablet behind
   the plant's shared address cannot block everybody). This table keeps those "known" (address, login) pairs across API
   restarts and application-pool recycles; before it existed they lived only in memory (Fable review).
   Rows older than 30 days are ignored and removed by the API. No personal data beyond the address and the typed login.
   ============================================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
IF OBJECT_ID(N'qa.LoginKnownClients', N'U') IS NULL
CREATE TABLE qa.LoginKnownClients (
    ClientKey      nvarchar(64)  NOT NULL,   -- IPv4 address, or IPv6 /64 network
    LoginKey       nvarchar(60)  NOT NULL,   -- the throttle's folded login key
    LastSuccessAt  datetime2(0)  NOT NULL,
    CONSTRAINT PK_LoginKnownClients PRIMARY KEY (ClientKey, LoginKey)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoginKnownClients_LastSuccess' AND object_id = OBJECT_ID(N'qa.LoginKnownClients'))
    CREATE INDEX IX_LoginKnownClients_LastSuccess ON qa.LoginKnownClients (LastSuccessAt);
GO
IF OBJECT_ID(N'qa.SchemaVersions', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM qa.SchemaVersions WHERE Script = N'16_upgrade_master_login_known_clients')
    INSERT INTO qa.SchemaVersions (Script) VALUES (N'16_upgrade_master_login_known_clients');
GO
PRINT N'16_upgrade_master_login_known_clients.sql complete.';
