-- Explicit document lifecycle state. Failure details are stable codes only; exception text stays in logs.

IF COL_LENGTH(N'dbo.Documents', N'Status') IS NULL
    ALTER TABLE dbo.Documents ADD Status nvarchar(32) NOT NULL
        CONSTRAINT DF_Documents_Status DEFAULT (N'Queued');
IF COL_LENGTH(N'dbo.Documents', N'StatusUpdatedAt') IS NULL
    ALTER TABLE dbo.Documents ADD StatusUpdatedAt datetime2 NOT NULL
        CONSTRAINT DF_Documents_StatusUpdatedAt DEFAULT (SYSUTCDATETIME());
IF COL_LENGTH(N'dbo.Documents', N'FailureCode') IS NULL
    ALTER TABLE dbo.Documents ADD FailureCode nvarchar(64) NULL;
GO

-- Rows indexed before this migration have chunks; everything else remains Queued and is resolved by
-- the pipeline or, if nothing progresses, by the Worker's stale-processing sweep.
UPDATE dbo.Documents
SET Status = N'Indexed', StatusUpdatedAt = COALESCE(IndexedAt, StatusUpdatedAt)
WHERE ChunkCount > 0 AND Status = N'Queued';

-- Unindexed rows keep their upload time so the stale sweep measures real age, not migration time.
UPDATE dbo.Documents
SET StatusUpdatedAt = CreatedAt
WHERE ChunkCount = 0 AND Status = N'Queued' AND CreatedAt < StatusUpdatedAt;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Documents_Status')
    ALTER TABLE dbo.Documents ADD CONSTRAINT CK_Documents_Status
        CHECK (Status IN (N'Queued', N'Extracting', N'Processing', N'Indexed', N'NoText', N'Failed'));
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Documents_FailureCode')
    ALTER TABLE dbo.Documents ADD CONSTRAINT CK_Documents_FailureCode
        CHECK ((Status = N'Failed' AND FailureCode IS NOT NULL) OR (Status <> N'Failed' AND FailureCode IS NULL));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Documents') AND name = N'IX_Documents_Status')
    CREATE INDEX IX_Documents_Status ON dbo.Documents (Status, StatusUpdatedAt) INCLUDE (DeletedAt);
GO
