-- Least-privilege application roles. The migration runner adds the API and Worker managed identities
-- to these roles on every run, so grants are never applied by hand.

IF DATABASE_PRINCIPAL_ID(N'docintake_api') IS NULL
    CREATE ROLE docintake_api AUTHORIZATION dbo;
IF DATABASE_PRINCIPAL_ID(N'docintake_worker') IS NULL
    CREATE ROLE docintake_worker AUTHORIZATION dbo;
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Documents TO docintake_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Documents TO docintake_worker;
GO
