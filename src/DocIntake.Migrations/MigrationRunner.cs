using System.Data;
using Azure.Core;
using Microsoft.Data.SqlClient;

namespace DocIntake.Migrations;

public enum MigrationMode
{
    Apply,
    Verify
}

public sealed class MigrationRunner(
    string connectionString,
    TokenCredential credential,
    TextWriter log)
{
    private const string LockResource = "DocIntake.Migrations";
    private const int CommandTimeoutSeconds = 600;
    private static readonly string[] SqlScopes = ["https://database.windows.net/.default"];

    internal const string EnsureJournalSql = """
        IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
            CREATE TABLE dbo.SchemaVersions
            (
                ScriptName nvarchar(256) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
                Checksum   char(64)      NOT NULL,
                AppliedAt  datetime2     NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT (SYSUTCDATETIME()),
                AppliedBy  nvarchar(128) NOT NULL CONSTRAINT DF_SchemaVersions_AppliedBy DEFAULT (SUSER_SNAME())
            );
        """;

    // Adopts a user that already maps to the identity (for example one created manually with
    // FROM EXTERNAL PROVIDER) and refuses to silently re-point a name that maps to another identity.
    internal const string EnsurePrincipalSql = """
        SET XACT_ABORT ON;
        DECLARE @UserName sysname = (SELECT name FROM sys.database_principals WHERE sid = @Sid);
        IF @UserName IS NULL
        BEGIN
            IF DATABASE_PRINCIPAL_ID(@Name) IS NOT NULL
                THROW 51011, 'A database principal with this name exists for a different identity.', 1;
            DECLARE @create nvarchar(max) =
                N'CREATE USER ' + QUOTENAME(@Name) + N' WITH SID = ' + CONVERT(nvarchar(max), @Sid, 1) + N', TYPE = E;';
            EXEC sys.sp_executesql @create;
            SET @UserName = @Name;
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.database_role_members members
            JOIN sys.database_principals roles ON roles.principal_id = members.role_principal_id
            JOIN sys.database_principals users ON users.principal_id = members.member_principal_id
            WHERE roles.name = @Role AND users.name = @UserName
        )
        BEGIN
            DECLARE @grant nvarchar(max) = N'ALTER ROLE ' + QUOTENAME(@Role) + N' ADD MEMBER ' + QUOTENAME(@UserName) + N';';
            EXEC sys.sp_executesql @grant;
        END;

        SELECT @UserName;
        """;

    internal const string VerifyPrincipalSql = """
        SELECT CASE WHEN EXISTS
        (
            SELECT 1
            FROM sys.database_principals users
            JOIN sys.database_role_members members ON members.member_principal_id = users.principal_id
            JOIN sys.database_principals roles ON roles.principal_id = members.role_principal_id
            WHERE users.sid = @Sid AND roles.name = @Role
        ) THEN 1 ELSE 0 END;
        """;

    public async Task<int> RunAsync(
        MigrationMode mode,
        IReadOnlyList<MigrationScript> scripts,
        IReadOnlyList<DatabasePrincipal> principals,
        CancellationToken ct)
    {
        await using var connection = await OpenWithRetryAsync(ct).ConfigureAwait(false);
        await AcquireLockAsync(connection, ct).ConfigureAwait(false);

        if (mode == MigrationMode.Apply)
        {
            await ExecuteAsync(connection, null, EnsureJournalSql, ct).ConfigureAwait(false);
        }

        var applied = await ReadJournalAsync(connection, ct).ConfigureAwait(false);
        var pending = MigrationPlanner.GetPending(scripts, applied);
        log.WriteLine($"Migrations: {applied.Count} applied, {pending.Count} pending.");

        if (mode == MigrationMode.Verify)
        {
            var problems = pending.Select(script => $"pending migration {script.Name}").ToList();
            foreach (var principal in principals)
            {
                if (!await IsPrincipalGrantedAsync(connection, principal, ct).ConfigureAwait(false))
                {
                    problems.Add($"identity {principal.Name} is not a member of {principal.Role}");
                }
            }

            if (problems.Count > 0)
            {
                throw new MigrationDriftException("Database is not at the expected state: " + string.Join("; ", problems) + ".");
            }

            log.WriteLine("Database schema and application grants match this build.");
            return 0;
        }

        foreach (var script in pending)
        {
            log.WriteLine($"Applying {script.Name} ({script.Batches.Count} batches)...");
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var batch in script.Batches)
                {
                    await ExecuteAsync(connection, transaction, batch, ct).ConfigureAwait(false);
                }

                await using var journal = new SqlCommand(
                    "INSERT dbo.SchemaVersions (ScriptName, Checksum) VALUES (@Name, @Checksum);",
                    connection, transaction);
                journal.Parameters.Add("@Name", SqlDbType.NVarChar, 256).Value = script.Name;
                journal.Parameters.Add("@Checksum", SqlDbType.Char, 64).Value = script.Checksum;
                await journal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        foreach (var principal in principals)
        {
            await using var command = new SqlCommand(EnsurePrincipalSql, connection) { CommandTimeout = CommandTimeoutSeconds };
            AddPrincipalParameters(command, principal);
            command.Parameters.Add("@Name", SqlDbType.NVarChar, 128).Value = principal.Name;
            var userName = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            log.WriteLine($"Database user {userName} (client ID {principal.ClientId}) is a member of {principal.Role}.");
        }

        log.WriteLine("Database migrations completed.");
        return 0;
    }

    private async Task<SqlConnection> OpenWithRetryAsync(CancellationToken ct)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            var connection = new SqlConnection(connectionString);
            connection.AccessTokenCallback = async (_, cancellationToken) =>
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext(SqlScopes), cancellationToken)
                    .ConfigureAwait(false);
                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            };

            try
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                return connection;
            }
            catch (SqlException exception) when (attempt < maxAttempts)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                var delay = TimeSpan.FromSeconds(10 * attempt);
                log.WriteLine($"SQL connection attempt {attempt} failed ({exception.Number}); retrying in {delay.TotalSeconds}s.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task AcquireLockAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand("sys.sp_getapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = CommandTimeoutSeconds
        };
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = LockResource;
        command.Parameters.Add("@LockMode", SqlDbType.VarChar, 32).Value = "Exclusive";
        command.Parameters.Add("@LockOwner", SqlDbType.VarChar, 32).Value = "Session";
        command.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = 120_000;
        var result = command.Parameters.Add("@Result", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if ((int)result.Value < 0)
        {
            throw new InvalidOperationException("Another migration run holds the migration lock.");
        }
    }

    private static async Task<IReadOnlyCollection<AppliedMigration>> ReadJournalAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NOT NULL
                SELECT ScriptName, Checksum FROM dbo.SchemaVersions;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var applied = new List<AppliedMigration>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            applied.Add(new AppliedMigration(reader.GetString(0), reader.GetString(1)));
        }

        return applied;
    }

    private static async Task<bool> IsPrincipalGrantedAsync(SqlConnection connection, DatabasePrincipal principal, CancellationToken ct)
    {
        await using var command = new SqlCommand(VerifyPrincipalSql, connection);
        AddPrincipalParameters(command, principal);
        return (int)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0) == 1;
    }

    private static void AddPrincipalParameters(SqlCommand command, DatabasePrincipal principal)
    {
        command.Parameters.Add("@Sid", SqlDbType.VarBinary, 85).Value = principal.Sid;
        command.Parameters.Add("@Role", SqlDbType.NVarChar, 128).Value = principal.Role;
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
