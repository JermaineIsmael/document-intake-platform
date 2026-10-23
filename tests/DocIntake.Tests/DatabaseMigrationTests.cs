using DocIntake.Migrations;
using FluentAssertions;

namespace DocIntake.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public void Embedded_scripts_are_shipped_in_version_order()
    {
        var scripts = MigrationScripts.LoadEmbedded();

        scripts.Select(script => script.Name).Should().StartWith(
            ["0001_documents_baseline.sql", "0002_document_status.sql", "0003_application_roles.sql"]);
        scripts.Select(script => script.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);
        scripts.Should().OnlyContain(script => script.Checksum.Length == 64 && script.Batches.Count > 0);
    }

    [Fact]
    public void Status_migration_constrains_status_to_every_lifecycle_value()
    {
        var script = string.Join('\n', MigrationScripts.LoadEmbedded()
            .Single(s => s.Name == "0002_document_status.sql").Batches);

        foreach (var status in Enum.GetNames<DocIntake.Shared.DocumentStatus>())
        {
            script.Should().Contain($"N'{status}'", "the CHECK constraint must accept every status the code writes");
        }
    }

    [Fact]
    public void Batches_split_on_go_lines_only()
    {
        var batches = MigrationScripts.SplitBatches(
            "CREATE TABLE a (GOAL int);\nGO\n-- comment only\nGO -- trailing\n  go  \nSELECT 1;");

        batches.Should().Equal("CREATE TABLE a (GOAL int);", "SELECT 1;");
    }

    [Fact]
    public void Checksum_ignores_line_endings_and_trailing_whitespace()
    {
        var unix = MigrationScripts.Create("0009_x.sql", "SELECT 1;\nSELECT 2;\n");
        var windows = MigrationScripts.Create("0009_x.sql", "SELECT 1;   \r\nSELECT 2;\r\n\r\n");
        var changed = MigrationScripts.Create("0009_x.sql", "SELECT 1;\nSELECT 3;\n");

        windows.Checksum.Should().Be(unix.Checksum);
        changed.Checksum.Should().NotBe(unix.Checksum);
    }

    [Theory]
    [InlineData("1_baseline.sql")]
    [InlineData("0001_Baseline.sql")]
    [InlineData("0001 baseline.sql")]
    public void Script_names_must_be_versioned(string name)
    {
        var act = () => MigrationScripts.Create(name, "SELECT 1;");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Duplicate_versions_are_rejected()
    {
        var act = () => MigrationScripts.Order(
        [
            MigrationScripts.Create("0001_a.sql", "SELECT 1;"),
            MigrationScripts.Create("0001_b.sql", "SELECT 2;")
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*0001*");
    }

    [Fact]
    public void Planner_returns_only_unapplied_scripts()
    {
        var scripts = Scripts();

        var pending = MigrationPlanner.GetPending(scripts, [new AppliedMigration(scripts[0].Name, scripts[0].Checksum)]);

        pending.Select(script => script.Name).Should().Equal("0002_b.sql", "0003_c.sql");
    }

    [Fact]
    public void Planner_detects_a_modified_applied_script()
    {
        var scripts = Scripts();

        var act = () => MigrationPlanner.GetPending(scripts, [new AppliedMigration(scripts[0].Name, new string('0', 64))]);

        act.Should().Throw<MigrationDriftException>().WithMessage("*checksum*");
    }

    [Fact]
    public void Planner_detects_a_database_newer_than_the_build()
    {
        var act = () => MigrationPlanner.GetPending(Scripts(), [new AppliedMigration("0099_future.sql", new string('A', 64))]);

        act.Should().Throw<MigrationDriftException>().WithMessage("*not shipped*");
    }

    [Fact]
    public void Planner_rejects_a_new_script_numbered_before_the_latest_applied()
    {
        var scripts = Scripts();

        var act = () => MigrationPlanner.GetPending(scripts, [new AppliedMigration(scripts[2].Name, scripts[2].Checksum)]);

        act.Should().Throw<MigrationDriftException>().WithMessage("*renumber*");
    }

    [Fact]
    public void Principal_sid_is_the_client_id_bytes()
    {
        var clientId = Guid.NewGuid();

        var principal = DatabasePrincipal.Create("di-dev-api-sql", clientId.ToString(), "docintake_api");

        principal.Sid.Should().Equal(clientId.ToByteArray());
    }

    [Theory]
    [InlineData("bad]name", "6f8a3f1e-2b7c-4b8e-9a51-0d2c9e7b4a11", "docintake_api")]
    [InlineData("api", "not-a-guid", "docintake_api")]
    [InlineData("api", "00000000-0000-0000-0000-000000000000", "docintake_api")]
    [InlineData("api", "6f8a3f1e-2b7c-4b8e-9a51-0d2c9e7b4a11", "db_owner")]
    public void Principal_validation_rejects_unsafe_values(string name, string clientId, string role)
    {
        var act = () => DatabasePrincipal.Create(name, clientId, role);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Settings_read_job_environment()
    {
        var apiClientId = Guid.NewGuid().ToString();
        var workerClientId = Guid.NewGuid().ToString();
        var environment = new Dictionary<string, string?>
        {
            ["Sql__Server"] = "server.database.windows.net",
            ["Sql__Database"] = "docintake",
            ["Sql__ManagedIdentityClientId"] = Guid.NewGuid().ToString(),
            ["Migrations__ApiPrincipalName"] = "di-api-sql",
            ["Migrations__ApiClientId"] = apiClientId,
            ["Migrations__WorkerPrincipalName"] = "di-worker-sql",
            ["Migrations__WorkerClientId"] = workerClientId
        };

        var settings = MigrationSettings.FromEnvironment(key => environment.GetValueOrDefault(key), []);

        settings.Mode.Should().Be(MigrationMode.Apply);
        settings.Principals.Select(p => (p.Name, p.ClientId.ToString(), p.Role)).Should().Equal(
            ("di-api-sql", apiClientId, "docintake_api"),
            ("di-worker-sql", workerClientId, "docintake_worker"));
        MigrationSettings.FromEnvironment(key => environment.GetValueOrDefault(key), ["--verify"])
            .Mode.Should().Be(MigrationMode.Verify);
    }

    [Fact]
    public void Settings_reject_a_connection_string_as_server()
    {
        var act = () => MigrationSettings.FromEnvironment(key => key switch
        {
            "Sql__Server" => "Server=x;Password=y",
            "Sql__Database" => "docintake",
            _ => null
        }, []);

        act.Should().Throw<InvalidOperationException>();
    }

    private static IReadOnlyList<MigrationScript> Scripts() => MigrationScripts.Order(
    [
        MigrationScripts.Create("0001_a.sql", "SELECT 1;"),
        MigrationScripts.Create("0002_b.sql", "SELECT 2;"),
        MigrationScripts.Create("0003_c.sql", "SELECT 3;")
    ]);
}
