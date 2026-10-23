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
        // The real, versioned migrations build the schema, so these tests also exercise every script.
        foreach (var batch in DocIntake.Migrations.MigrationScripts.LoadEmbedded().SelectMany(script => script.Batches))
        {
            await ExecuteAsync(_database, batch);
        }
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

        var ownerId = "00000000-0000-0000-0000-000000000001";
        var sourceBlobName = $"{id}/a.pdf";
        await repository.SaveAsync(id, "https://st/documents/a.pdf", sourceBlobName,
            ownerId, DateTimeOffset.UnixEpoch, 3, CancellationToken.None);
        time.Now = time.Now.AddMinutes(5);
        var redelivery = () => repository.SaveAsync(id, "https://st/documents/a.pdf", sourceBlobName,
            ownerId, DateTimeOffset.UnixEpoch, 7, CancellationToken.None);

        await redelivery.Should().NotThrowAsync();
        var rows = await ReadRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Should().Be((id, "https://st/documents/a.pdf", sourceBlobName, ownerId, 7,
            new DateTime(1970, 1, 1, 0, 0, 0), new DateTime(2026, 1, 1, 0, 5, 0)));
    }

    [LocalDbFact]
    public async Task Concurrent_saves_for_a_new_document_do_not_violate_the_primary_key()
    {
        var repository = new DocumentMetadataRepository(ConnectionString(_database), credential: null);
        var id = Guid.NewGuid().ToString("D");

        var saves = Enumerable.Range(1, 8)
            .Select(n => Task.Run(() => repository.SaveAsync(id, "https://st/documents/a.pdf", $"{id}/a.pdf",
                "00000000-0000-0000-0000-000000000001", DateTimeOffset.UnixEpoch, n, CancellationToken.None)));
        var act = () => Task.WhenAll(saves);

        await act.Should().NotThrowAsync();
        (await ReadRowsAsync()).Should().ContainSingle().Which.ChunkCount.Should().BeInRange(1, 8);
    }

    [LocalDbFact]
    public async Task Overlong_values_are_rejected_instead_of_silently_truncated()
    {
        var repository = new DocumentMetadataRepository(ConnectionString(_database), credential: null);

        var longId = () => repository.SaveAsync(new string('a', 65), "https://st/a", "id/a.pdf",
            "00000000-0000-0000-0000-000000000001", DateTimeOffset.UtcNow, 1, CancellationToken.None);
        var longUrl = () => repository.SaveAsync("id", "https://st/" + new string('a', 2048), "id/a.pdf",
            "00000000-0000-0000-0000-000000000001", DateTimeOffset.UtcNow, 1, CancellationToken.None);

        await longId.Should().ThrowAsync<ArgumentException>();
        await longUrl.Should().ThrowAsync<ArgumentException>();
        (await ReadRowsAsync()).Should().BeEmpty();
    }

    [LocalDbFact]
    public async Task Status_updates_only_move_forward_and_success_supersedes_failure()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new DocumentMetadataRepository(ConnectionString(_database), credential: null, time);
        var id = Guid.NewGuid().ToString("D");
        const string owner = "00000000-0000-0000-0000-000000000001";
        await InsertQueuedAsync(id, owner, time.Now.UtcDateTime);

        (await repository.TryUpdateStatusAsync(id, owner, DocIntake.Shared.DocumentStatus.Processing, null, default))
            .Should().BeTrue();
        (await repository.TryUpdateStatusAsync(id, owner, DocIntake.Shared.DocumentStatus.Extracting, null, default))
            .Should().BeFalse("a late Extracting event must not move the document backwards");
        (await repository.TryUpdateStatusAsync(id, Guid.NewGuid().ToString("D"),
            DocIntake.Shared.DocumentStatus.Failed, "IndexingFailed", default)).Should().BeFalse("wrong owner");
        (await repository.TryUpdateStatusAsync(id, owner, DocIntake.Shared.DocumentStatus.Failed, "IndexingFailed", default))
            .Should().BeTrue();
        (await ReadStatusAsync(id)).Should().Be(("Failed", "IndexingFailed"));

        await repository.SaveAsync(id, "https://st/documents/a.pdf", $"{id}/a.pdf", owner,
            DateTimeOffset.UnixEpoch, 0, default);

        (await ReadStatusAsync(id)).Should().Be(("NoText", (string?)null));
        (await repository.TryUpdateStatusAsync(id, owner, DocIntake.Shared.DocumentStatus.Failed, "IndexingFailed", default))
            .Should().BeFalse("terminal results are not replaced by later failures");
    }

    [LocalDbFact]
    public async Task Stale_in_flight_documents_time_out_but_recent_and_terminal_ones_do_not()
    {
        var now = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var repository = new DocumentMetadataRepository(ConnectionString(_database), credential: null);
        const string owner = "00000000-0000-0000-0000-000000000001";
        var stale = Guid.NewGuid().ToString("D");
        var recent = Guid.NewGuid().ToString("D");
        var indexed = Guid.NewGuid().ToString("D");
        await InsertQueuedAsync(stale, owner, now.AddHours(-27).UtcDateTime);
        await InsertQueuedAsync(recent, owner, now.AddHours(-1).UtcDateTime);
        await InsertQueuedAsync(indexed, owner, now.AddHours(-30).UtcDateTime, status: "Indexed");

        var count = await repository.FailStaleAsync(now, TimeSpan.FromHours(26), 100, default);

        count.Should().Be(1);
        (await ReadStatusAsync(stale)).Should().Be(("Failed", "ProcessingTimedOut"));
        (await ReadStatusAsync(recent)).Should().Be(("Queued", (string?)null));
        (await ReadStatusAsync(indexed)).Should().Be(("Indexed", (string?)null));
    }

    [LocalDbFact]
    public async Task Schema_rejects_unknown_statuses_and_inconsistent_failure_codes()
    {
        var id = Guid.NewGuid().ToString("D");
        await InsertQueuedAsync(id, "owner", DateTime.UtcNow);

        var unknown = () => ExecuteAsync(_database, $"UPDATE dbo.Documents SET Status = N'Done' WHERE Id = N'{id}'");
        var failedWithoutCode = () => ExecuteAsync(_database,
            $"UPDATE dbo.Documents SET Status = N'Failed' WHERE Id = N'{id}'");

        await unknown.Should().ThrowAsync<SqlException>();
        await failedWithoutCode.Should().ThrowAsync<SqlException>();
    }

    private async Task InsertQueuedAsync(string id, string ownerId, DateTime at, string status = "Queued")
    {
        await using var connection = new SqlConnection(ConnectionString(_database));
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT dbo.Documents (Id, SourceUrl, BlobName, OwnerId, ChunkCount, CreatedAt, Status, StatusUpdatedAt)
            VALUES (@Id, N'https://st/documents/a.pdf', @Id + N'/a.pdf', @OwnerId, 0, @At, @Status, @At);
            """, connection);
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@OwnerId", ownerId);
        command.Parameters.Add("@At", System.Data.SqlDbType.DateTime2).Value = at;
        command.Parameters.AddWithValue("@Status", status);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(string Status, string? FailureCode)> ReadStatusAsync(string id)
    {
        await using var connection = new SqlConnection(ConnectionString(_database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Status, FailureCode FROM dbo.Documents WHERE Id = @Id", connection);
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<List<(string Id, string SourceUrl, string BlobName, string OwnerId, int ChunkCount,
        DateTime CreatedAt, DateTime IndexedAt)>> ReadRowsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString(_database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, SourceUrl, BlobName, OwnerId, ChunkCount, CreatedAt, IndexedAt FROM dbo.Documents", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, string, string, string, int, DateTime, DateTime)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetDateTime(5), reader.GetDateTime(6)));
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
