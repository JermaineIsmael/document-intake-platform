using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;

namespace DocIntake.Migrations;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var settings = MigrationSettings.FromEnvironment(Environment.GetEnvironmentVariable, args);
            TokenCredential credential = settings.ManagedIdentityClientId is { } clientId
                ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
                : new DefaultAzureCredential();
            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = $"tcp:{settings.Server},1433",
                InitialCatalog = settings.Database,
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = false,
                ConnectTimeout = 30
            }.ConnectionString;

            Console.WriteLine($"Running database migrations in {settings.Mode} mode against {settings.Server}/{settings.Database}.");
            var runner = new MigrationRunner(connectionString, credential, Console.Out);
            return await runner.RunAsync(settings.Mode, MigrationScripts.LoadEmbedded(), settings.Principals, cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Database migration failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }
}

public sealed record MigrationSettings(
    string Server,
    string Database,
    string? ManagedIdentityClientId,
    MigrationMode Mode,
    IReadOnlyList<DatabasePrincipal> Principals)
{
    public static MigrationSettings FromEnvironment(Func<string, string?> get, IReadOnlyList<string> args)
    {
        var server = Require(get, "Sql__Server");
        if (server.Contains(';', StringComparison.Ordinal) || server.Contains('=', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sql__Server must be a server host name, not a connection string.");
        }

        var modeValue = args.Contains("--verify", StringComparer.Ordinal) ? "Verify" : get("Migrations__Mode") ?? "Apply";
        if (!Enum.TryParse<MigrationMode>(modeValue, ignoreCase: true, out var mode))
        {
            throw new InvalidOperationException("Migrations__Mode must be Apply or Verify.");
        }

        var clientId = get("Sql__ManagedIdentityClientId");
        if (!string.IsNullOrWhiteSpace(clientId) && !Guid.TryParse(clientId, out _))
        {
            throw new InvalidOperationException("Sql__ManagedIdentityClientId must be a GUID.");
        }

        var principals = new List<DatabasePrincipal>();
        AddPrincipal(principals, get, "Api", "docintake_api");
        AddPrincipal(principals, get, "Worker", "docintake_worker");

        return new MigrationSettings(server, Require(get, "Sql__Database"),
            string.IsNullOrWhiteSpace(clientId) ? null : clientId, mode, principals);
    }

    // Principals are optional for local runs; deployments always configure both.
    private static void AddPrincipal(List<DatabasePrincipal> principals, Func<string, string?> get, string prefix, string role)
    {
        var name = get($"Migrations__{prefix}PrincipalName");
        var clientId = get($"Migrations__{prefix}ClientId");
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        principals.Add(DatabasePrincipal.Create(name, clientId, role));
    }

    private static string Require(Func<string, string?> get, string key) =>
        get(key) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{key} must be configured.");
}
