-- DocIntake Sprint 3 schema. Run once against the "docintake" database while connected as a member
-- of the Entra admin group (sqlAdminGroupName). The server is Entra-only: there is no SQL login.
-- Example (go-sqlcmd): sqlcmd -S <sqlServerFqdn> -d docintake --authentication-method ActiveDirectoryDefault -i sql/schema.sql

IF OBJECT_ID(N'dbo.Documents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Documents
    (
        Id                  nvarchar(64)   NOT NULL CONSTRAINT PK_Documents PRIMARY KEY,
        SourceUrl           nvarchar(2048) NOT NULL,
        BlobName            nvarchar(2048) NULL,
        OwnerId             nvarchar(128)  NULL,
        ChunkCount          int            NOT NULL CONSTRAINT DF_Documents_ChunkCount DEFAULT (0),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_Documents_CreatedAt DEFAULT (SYSUTCDATETIME()),
        IndexedAt           datetime2      NULL,
        DeletedAt           datetime2      NULL,
        RecoverableUntil    datetime2      NULL,
        DeletionProcessedAt datetime2      NULL
    );
END;
GO

-- Idempotent migration from the original ownerless ingestion schema. Existing
-- rows keep OwnerId NULL and are intentionally inaccessible to user APIs.
IF COL_LENGTH(N'dbo.Documents', N'BlobName') IS NULL
    ALTER TABLE dbo.Documents ADD BlobName nvarchar(2048) NULL;
IF COL_LENGTH(N'dbo.Documents', N'OwnerId') IS NULL
    ALTER TABLE dbo.Documents ADD OwnerId nvarchar(128) NULL;
IF COL_LENGTH(N'dbo.Documents', N'CreatedAt') IS NULL
    ALTER TABLE dbo.Documents ADD CreatedAt datetime2 NOT NULL
        CONSTRAINT DF_Documents_CreatedAt_Migration DEFAULT (SYSUTCDATETIME());
IF COL_LENGTH(N'dbo.Documents', N'DeletedAt') IS NULL
    ALTER TABLE dbo.Documents ADD DeletedAt datetime2 NULL;
IF COL_LENGTH(N'dbo.Documents', N'RecoverableUntil') IS NULL
    ALTER TABLE dbo.Documents ADD RecoverableUntil datetime2 NULL;
IF COL_LENGTH(N'dbo.Documents', N'DeletionProcessedAt') IS NULL
    ALTER TABLE dbo.Documents ADD DeletionProcessedAt datetime2 NULL;
IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Documents') AND name = N'IndexedAt' AND is_nullable = 0
)
    ALTER TABLE dbo.Documents ALTER COLUMN IndexedAt datetime2 NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Documents') AND name = N'IX_Documents_OwnerId')
    CREATE INDEX IX_Documents_OwnerId ON dbo.Documents (OwnerId, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Documents') AND name = N'IX_Documents_Retention')
    CREATE INDEX IX_Documents_Retention ON dbo.Documents (DeletedAt, RecoverableUntil, CreatedAt);
GO

-- Worker identity database user.
--
-- Sprint 3: the worker runs locally under YOUR OWN Entra identity (DefaultAzureCredential picks up
-- az login / Visual Studio). If testing with the admin group account, this section can be skipped;
-- the admin group already has full access. If testing with a different Entra user, replace
-- <worker-identity-name> with that user's UPN or display name.
--
-- Replace <worker-identity-name> with the Worker Container App's system-assigned managed identity
-- name and run after deployment. The Worker receives only table-level access to dbo.Documents.
-- The managed identity's display name in Entra ID is typically the Container App resource name.
-- CREATE USER ... FROM EXTERNAL PROVIDER fails if the identity doesn't exist or the name doesn't match exactly.
IF DATABASE_PRINCIPAL_ID(N'<worker-identity-name>') IS NULL
BEGIN
    CREATE USER [<worker-identity-name>] FROM EXTERNAL PROVIDER;
END;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Documents TO [<worker-identity-name>];
GO

-- Replace <api-identity-name> with the API Container App's system-assigned managed identity
-- name and run after deployment. The API requires table-level access for owner-scoped status,
-- deletion, and restoration operations.
IF DATABASE_PRINCIPAL_ID(N'<api-identity-name>') IS NULL
BEGIN
    CREATE USER [<api-identity-name>] FROM EXTERNAL PROVIDER;
END;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Documents TO [<api-identity-name>];
GO
