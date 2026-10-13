using System.Runtime.InteropServices;
using DocIntake.Worker;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace DocIntake.Tests;

// Runs the real upsert SQL against SQL Server LocalDB (Windows only); skipped when LocalDB is absent.
public sealed class DocumentMetadataRepositoryTests : IAsyncLifetime
{
    private const string LocalDbServer = @"(localdb)\MSSQLLocalDB";
    private readonly string _database = $"DocIntakeTests_{Guid.NewGuid():N}";

    private string ConnectionString(string database) => new SqlConnectionStringBuilder
    {
        DataSource = LocalDbServer,
        InitialCatalog = database,
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        ConnectTimeout = 60
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (!LocalDbFactAttribute.IsAvailable)
        {
            return;
        }

        await ExecuteAsync("master", $"CREATE DATABASE [{_database}];");
        // Same table definition as sql/schema.sql.
        await ExecuteAsync(_database, """
            CREATE TABLE dbo.Documents
            (
                Id         nvarchar(64)   NOT NULL CONSTRAINT PK_Documents PRIMARY KEY,
                SourceUrl  nvarchar(2048) NOT NULL,
                ChunkCount int            NOT NULL,
                IndexedAt  datetime2      NOT NULL
            );
            """);
    }

    public async Task DisposeAsync()
    {
        if (!LocalDbFactAttribute.IsAvailable)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteAsync("master", $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];");
    }

    [LocalDbFact]
    public async Task Saving_the_same_document_twice_updates_the_single_row()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new DocumentMetadataRepository(ConnectionString(_database), credential: null, time);
        var id = Guid.NewGuid().ToString("D");

        await repository.SaveAsync(id, "https://st/documents/a.pdf", 3, CancellationToken.None);
        time.Now = time.Now.AddMinutes(5);
        var redelivery = () => repository.SaveAsync(id, "https://st/documents/a.pdf", 7, CancellationToken.None);

        await redelivery.Should().NotThrowAsync();
        var rows = await ReadRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Should().Be((id, "https://st/documents/a.pdf", 7, new DateTime(2026, 1, 1, 0, 5, 0)));
    }

    [LocalDbFact]
    public async Task Concurrent_saves_for_a_new_document_do_not_violate_the_primary_key()
    {
        var repository = new DocumentMetadataRepository(ConnectionString(_database), credential: null);
        var id = Guid.NewGuid().ToString("D");

        var saves = Enumerable.Range(1, 8)
            .Select(n => Task.Run(() => repository.SaveAsync(id, "https://st/documents/a.pdf", n, CancellationToken.None)));
        var act = () => Task.WhenAll(saves);

        await act.Should().NotThrowAsync();
        (await ReadRowsAsync()).Should().ContainSingle().Which.ChunkCount.Should().BeInRange(1, 8);
    }

    [LocalDbFact]
    public async Task Overlong_values_are_rejected_instead_of_silently_truncated()
    {
        var repository = new DocumentMetadataRepository(ConnectionString(_database), credential: null);

        var longId = () => repository.SaveAsync(new string('a', 65), "https://st/a", 1, CancellationToken.None);
        var longUrl = () => repository.SaveAsync("id", "https://st/" + new string('a', 2048), 1, CancellationToken.None);

        await longId.Should().ThrowAsync<ArgumentException>();
        await longUrl.Should().ThrowAsync<ArgumentException>();
        (await ReadRowsAsync()).Should().BeEmpty();
    }

    private async Task<List<(string Id, string SourceUrl, int ChunkCount, DateTime IndexedAt)>> ReadRowsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString(_database));
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Id, SourceUrl, ChunkCount, IndexedAt FROM dbo.Documents", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, string, int, DateTime)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetDateTime(3)));
        }

        return rows;
    }

    private async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(ConnectionString(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class LocalDbFactAttribute : FactAttribute
{
    public static readonly bool IsAvailable = Detect();

    public LocalDbFactAttribute()
    {
        if (!IsAvailable)
        {
            Skip = "SQL Server LocalDB is not installed.";
        }
    }

    // LocalDB installs to "C:\Program Files\Microsoft SQL Server\<version>\Tools\Binn\SqlLocalDB.exe".
    private static bool Detect()
    {
        const string root = @"C:\Program Files\Microsoft SQL Server";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !Directory.Exists(root))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateDirectories(root)
                .Any(dir => File.Exists(Path.Combine(dir, "Tools", "Binn", "SqlLocalDB.exe")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
