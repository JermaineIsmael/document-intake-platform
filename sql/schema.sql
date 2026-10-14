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
-- az login / Visual Studio). If testing with the admin group account, this section can be skipped;
-- the admin group already has full access. If testing with a different Entra user, replace
-- <worker-identity-name> with that user's UPN or display name.
--
-- Sprint 4: replace <worker-identity-name> with the worker Container App's system-assigned managed
-- identity name (set in Bicep as '${prefix}-worker') and run this section once the Container App exists.
-- The managed identity's display name in Entra ID is typically the Container App resource name.
-- CREATE USER ... FROM EXTERNAL PROVIDER fails if the identity doesn't exist or the name doesn't match exactly.
IF DATABASE_PRINCIPAL_ID(N'<worker-identity-name>') IS NULL
BEGIN
    CREATE USER [<worker-identity-name>] FROM EXTERNAL PROVIDER;
END;
ALTER ROLE db_datawriter ADD MEMBER [<worker-identity-name>];
ALTER ROLE db_datareader ADD MEMBER [<worker-identity-name>];
GO
