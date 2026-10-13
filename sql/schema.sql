-- DocIntake Sprint 3 schema. Run once against the "docintake" database while connected as a member
-- of the Entra admin group (sqlAdminGroupName). The server is Entra-only: there is no SQL login.
-- Example (go-sqlcmd): sqlcmd -S <sqlServerFqdn> -d docintake --authentication-method ActiveDirectoryDefault -i sql/schema.sql

IF OBJECT_ID(N'dbo.Documents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Documents
    (
        Id         nvarchar(64)   NOT NULL CONSTRAINT PK_Documents PRIMARY KEY,
        SourceUrl  nvarchar(2048) NOT NULL,
        ChunkCount int            NOT NULL,
        IndexedAt  datetime2      NOT NULL
    );
END;
GO

-- Worker identity database user.
--
-- Sprint 3: the worker runs locally under YOUR OWN Entra identity (DefaultAzureCredential picks up
-- az login / Visual Studio). Members of the admin group already have full access, so this section
-- is not required for local testing; if you test with a non-admin account, use its UPN here.
--
-- Sprint 4: replace <worker-identity-name> with the worker Container App's system-assigned managed
-- identity display name (the Container App name) and run this section once that identity exists.
-- CREATE USER ... FROM EXTERNAL PROVIDER fails if the name doesn't resolve to an Entra principal.
IF DATABASE_PRINCIPAL_ID(N'<worker-identity-name>') IS NULL
BEGIN
    CREATE USER [<worker-identity-name>] FROM EXTERNAL PROVIDER;
END;
ALTER ROLE db_datawriter ADD MEMBER [<worker-identity-name>];
ALTER ROLE db_datareader ADD MEMBER [<worker-identity-name>];
GO
