using System.Data;
using Azure.Core;
using Microsoft.Data.SqlClient;

namespace DocIntake.Worker;

public sealed record RetentionDocument(
    string Id,
    string SourceBlobName,
    string ExtractedTextBlobName,
    int ChunkCount);

public interface IDocumentMetadataRepository
{
    Task<bool> IsActiveAsync(string documentId, string ownerId, CancellationToken ct);
    Task SaveAsync(
        string documentId, string sourceUrl, string sourceBlobName, string ownerId,
        DateTimeOffset createdAt, int chunkCount, CancellationToken ct);
    Task MarkExpiredAsync(DateTimeOffset now, int retentionDays, int batchSize, CancellationToken ct);
    Task<IReadOnlyList<RetentionDocument>> GetPendingCleanupAsync(int batchSize, CancellationToken ct);
    Task MarkDeletionProcessedAsync(string documentId, DateTimeOffset processedAt, CancellationToken ct);
    Task<IReadOnlyList<RetentionDocument>> GetPurgeReadyAsync(DateTimeOffset now, int batchSize, CancellationToken ct);
    Task PurgeAsync(string documentId, CancellationToken ct);
}

public sealed class DocumentMetadataRepository : IDocumentMetadataRepository
{
    public const int MaxIdLength = 64;
    public const int MaxSourceUrlLength = 2048;
    public const int MaxBlobNameLength = 2048;

    private static readonly string[] SqlScopes = ["https://database.windows.net/.default"];

    internal const string UpsertSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        UPDATE dbo.Documents WITH (UPDLOCK, SERIALIZABLE)
           SET SourceUrl = @SourceUrl,
               BlobName = @BlobName,
               ChunkCount = @ChunkCount,
               IndexedAt = @IndexedAt
         WHERE Id = @Id AND OwnerId = @OwnerId AND DeletedAt IS NULL;

        IF @@ROWCOUNT = 0
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.Documents WHERE Id = @Id)
                THROW 51000, 'Document is deleted or belongs to a different owner.', 1;

            INSERT dbo.Documents (Id, SourceUrl, BlobName, OwnerId, ChunkCount, CreatedAt, IndexedAt)
            VALUES (@Id, @SourceUrl, @BlobName, @OwnerId, @ChunkCount, @CreatedAt, @IndexedAt);
        END;

        COMMIT TRANSACTION;
        """;

    private readonly string _connectionString;
    private readonly TokenCredential? _credential;
    private readonly TimeProvider _timeProvider;

    public DocumentMetadataRepository(string connectionString, TokenCredential? credential, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _credential = credential;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> IsActiveAsync(string documentId, string ownerId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM dbo.Documents WHERE Id = @Id AND OwnerId = @OwnerId AND DeletedAt IS NULL
            ) THEN 1 ELSE 0 END;
            """, connection);
        AddIdentity(command, documentId, ownerId);
        return (int)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0) == 1;
    }

    public async Task SaveAsync(
        string documentId, string sourceUrl, string sourceBlobName, string ownerId,
        DateTimeOffset createdAt, int chunkCount, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBlobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);
        EnsureLength(documentId, MaxIdLength, nameof(documentId));
        EnsureLength(sourceUrl, MaxSourceUrlLength, nameof(sourceUrl));
        EnsureLength(sourceBlobName, MaxBlobNameLength, nameof(sourceBlobName));

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(UpsertSql, connection);
        AddIdentity(command, documentId, ownerId);
        command.Parameters.Add("@SourceUrl", SqlDbType.NVarChar, MaxSourceUrlLength).Value = sourceUrl;
        command.Parameters.Add("@BlobName", SqlDbType.NVarChar, MaxBlobNameLength).Value = sourceBlobName;
        command.Parameters.Add("@ChunkCount", SqlDbType.Int).Value = chunkCount;
        command.Parameters.Add("@CreatedAt", SqlDbType.DateTime2).Value = createdAt.UtcDateTime;
        command.Parameters.Add("@IndexedAt", SqlDbType.DateTime2).Value = _timeProvider.GetUtcNow().UtcDateTime;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkExpiredAsync(DateTimeOffset now, int retentionDays, int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionDays);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            ;WITH expired AS
            (
                SELECT TOP (@BatchSize) *
                FROM dbo.Documents WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE DeletedAt IS NULL AND CreatedAt <= @Cutoff
                ORDER BY CreatedAt
            )
            UPDATE expired
            SET DeletedAt = @Now, RecoverableUntil = @Now, DeletionProcessedAt = NULL;
            """, connection);
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
        command.Parameters.Add("@Cutoff", SqlDbType.DateTime2).Value = now.AddDays(-retentionDays).UtcDateTime;
        command.Parameters.Add("@Now", SqlDbType.DateTime2).Value = now.UtcDateTime;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RetentionDocument>> GetPendingCleanupAsync(int batchSize, CancellationToken ct) =>
        QueryRetentionAsync("""
            SELECT TOP (@BatchSize) Id, BlobName, SourceUrl, ChunkCount
            FROM dbo.Documents
            WHERE DeletedAt IS NOT NULL AND DeletionProcessedAt IS NULL
            ORDER BY DeletedAt;
            """, batchSize, ct);

    public async Task MarkDeletionProcessedAsync(string documentId, DateTimeOffset processedAt, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            UPDATE dbo.Documents SET DeletionProcessedAt = @ProcessedAt
            WHERE Id = @Id AND DeletedAt IS NOT NULL;
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, MaxIdLength).Value = documentId;
        command.Parameters.Add("@ProcessedAt", SqlDbType.DateTime2).Value = processedAt.UtcDateTime;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RetentionDocument>> GetPurgeReadyAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct) =>
        QueryRetentionAsync("""
            SELECT TOP (@BatchSize) Id, BlobName, SourceUrl, ChunkCount
            FROM dbo.Documents
            WHERE DeletedAt IS NOT NULL AND DeletionProcessedAt IS NOT NULL
              AND RecoverableUntil <= @Now
            ORDER BY RecoverableUntil;
            """, batchSize, ct, now);

    public async Task PurgeAsync(string documentId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            DELETE dbo.Documents
            WHERE Id = @Id AND DeletedAt IS NOT NULL AND DeletionProcessedAt IS NOT NULL
              AND RecoverableUntil <= SYSUTCDATETIME();
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, MaxIdLength).Value = documentId;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RetentionDocument>> QueryRetentionAsync(
        string sql, int batchSize, CancellationToken ct, DateTimeOffset? now = null)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
        if (now is { } time)
        {
            command.Parameters.Add("@Now", SqlDbType.DateTime2).Value = time.UtcDateTime;
        }

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<RetentionDocument>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var blobName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(blobName)
                && Uri.TryCreate(reader.GetString(2), UriKind.Absolute, out var sourceUri))
            {
                var segments = sourceUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    blobName = $"{Uri.UnescapeDataString(segments[^2])}/{Uri.UnescapeDataString(segments[^1])}";
                }
            }

            result.Add(new RetentionDocument(
                id, blobName, $"{id}/content.txt", reader.GetInt32(3)));
        }

        return result;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        if (_credential is { } credential)
        {
            connection.AccessTokenCallback = async (_, cancellationToken) =>
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext(SqlScopes), cancellationToken)
                    .ConfigureAwait(false);
                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            };
        }

        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static void AddIdentity(SqlCommand command, string id, string ownerId)
    {
        command.Parameters.Add("@Id", SqlDbType.NVarChar, MaxIdLength).Value = id;
        command.Parameters.Add("@OwnerId", SqlDbType.NVarChar, 128).Value = ownerId;
    }

    private static void EnsureLength(string value, int max, string parameterName)
    {
        if (value.Length > max)
        {
            throw new ArgumentException($"Value exceeds {max} characters.", parameterName);
        }
    }
}
