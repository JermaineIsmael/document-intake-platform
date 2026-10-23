-- Baseline: dbo.Documents as previously created by sql/schema.sql. Every statement is guarded so an
-- existing database that was bootstrapped manually is adopted without changes.

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

-- Upgrade path from the original ownerless ingestion schema. Existing rows keep OwnerId NULL and
-- are intentionally inaccessible to user APIs.
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
