using System.Data;
using System.Collections.Concurrent;
using Azure.Core;
using DocIntake.Shared;
using Microsoft.Data.SqlClient;

namespace DocIntake.Api.Services;

public sealed record DocumentMetadata(
    string Id,
    string SourceUrl,
    string BlobName,
    string OwnerId,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IndexedAt,
    DateTimeOffset? DeletedAt,
    DateTimeOffset? RecoverableUntil,
    DateTimeOffset? DeletionProcessedAt,
    DocumentStatus Status = DocumentStatus.Queued,
    DateTimeOffset? StatusUpdatedAt = null,
    string? FailureCode = null);

public interface IDocumentMetadataStore
{
    Task RegisterAsync(DocumentMetadata document, CancellationToken ct);
    Task RemovePendingAsync(string id, string ownerId, CancellationToken ct);
    Task<DocumentMetadata?> GetOwnedAsync(string id, string ownerId, CancellationToken ct);
    Task MarkDeletedAsync(string id, string ownerId, DateTimeOffset deletedAt, DateTimeOffset recoverableUntil, CancellationToken ct);
    Task MarkDeletionProcessedAsync(string id, string ownerId, DateTimeOffset processedAt, CancellationToken ct);
    Task RestoreAsync(string id, string ownerId, DateTimeOffset now, CancellationToken ct);
}

public sealed class InMemoryDocumentMetadataStore : IDocumentMetadataStore
{
    private readonly ConcurrentDictionary<string, DocumentMetadata> _documents = new(StringComparer.Ordinal);

    public Task RegisterAsync(DocumentMetadata document, CancellationToken ct)
    {
        var registered = document with
        {
            Status = DocumentStatus.Queued,
            StatusUpdatedAt = document.CreatedAt,
            FailureCode = null
        };
        if (!_documents.TryAdd(document.Id, registered))
        {
            throw new InvalidOperationException("Document ID already exists.");
        }

        return Task.CompletedTask;
    }

    // Development/test hook standing in for the Worker, which owns status transitions in SQL.
    public bool TrySetStatus(string id, DocumentStatus status, string? failureCode, DateTimeOffset at,
        int? chunkCount = null)
    {
        if (!_documents.TryGetValue(id, out var document)
            || !DocumentStatusTransitions.CanTransition(document.Status, status))
        {
            return false;
        }

        _documents[id] = document with
        {
            Status = status,
            StatusUpdatedAt = at,
            FailureCode = status == DocumentStatus.Failed ? failureCode : null,
            ChunkCount = chunkCount ?? document.ChunkCount,
            IndexedAt = status is DocumentStatus.Indexed or DocumentStatus.NoText ? at : document.IndexedAt
        };
        return true;
    }

    public Task<DocumentMetadata?> GetOwnedAsync(string id, string ownerId, CancellationToken ct)
    {
        _documents.TryGetValue(id, out var document);
        return Task.FromResult(document?.OwnerId == ownerId ? document : null);
    }

    public Task RemovePendingAsync(string id, string ownerId, CancellationToken ct)
    {
        if (_documents.TryGetValue(id, out var document)
            && document.OwnerId == ownerId
            && document.ChunkCount == 0)
        {
            _documents.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    public Task MarkDeletedAsync(string id, string ownerId, DateTimeOffset deletedAt,
        DateTimeOffset recoverableUntil, CancellationToken ct)
    {
        _documents.AddOrUpdate(id,
            _ => throw new InvalidOperationException("Document not found."),
            (_, document) => document.OwnerId != ownerId ? document : document with
            {
                DeletedAt = document.DeletedAt ?? deletedAt,
                RecoverableUntil = document.RecoverableUntil ?? recoverableUntil,
                DeletionProcessedAt = null
            });
        return Task.CompletedTask;
    }

    public Task MarkDeletionProcessedAsync(string id, string ownerId, DateTimeOffset processedAt, CancellationToken ct)
    {
        _documents.AddOrUpdate(id,
            _ => throw new InvalidOperationException("Document not found."),
            (_, document) => document.OwnerId != ownerId ? document : document with { DeletionProcessedAt = processedAt });
        return Task.CompletedTask;
    }

    public Task RestoreAsync(string id, string ownerId, DateTimeOffset now, CancellationToken ct)
    {
        if (!_documents.TryGetValue(id, out var document)
            || document.OwnerId != ownerId
            || document.RecoverableUntil is not { } recoverableUntil
            || recoverableUntil <= now)
        {
            throw new InvalidOperationException("Document is not within its recovery window.");
        }

        _documents[id] = document with { DeletedAt = null, RecoverableUntil = null, DeletionProcessedAt = null };
        return Task.CompletedTask;
    }
}

public sealed class UnavailableDocumentMetadataStore(string reason) : IDocumentMetadataStore
{
    private InvalidOperationException Unavailable() => new(reason);

    public Task RegisterAsync(DocumentMetadata document, CancellationToken ct) => throw Unavailable();
    public Task RemovePendingAsync(string id, string ownerId, CancellationToken ct) => throw Unavailable();
    public Task<DocumentMetadata?> GetOwnedAsync(string id, string ownerId, CancellationToken ct) => throw Unavailable();
    public Task MarkDeletedAsync(string id, string ownerId, DateTimeOffset deletedAt,
        DateTimeOffset recoverableUntil, CancellationToken ct) => throw Unavailable();
    public Task MarkDeletionProcessedAsync(string id, string ownerId, DateTimeOffset processedAt,
        CancellationToken ct) => throw Unavailable();
    public Task RestoreAsync(string id, string ownerId, DateTimeOffset now, CancellationToken ct) => throw Unavailable();
}

public sealed class SqlDocumentMetadataStore(string connectionString, TokenCredential? credential) : IDocumentMetadataStore
{
    private static readonly string[] SqlScopes = ["https://database.windows.net/.default"];

    public async Task RegisterAsync(DocumentMetadata document, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.SourceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.BlobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.OwnerId);
        if (document.SourceUrl.Length > 2048 || document.BlobName.Length > 2048 || document.OwnerId.Length > 128)
        {
            throw new ArgumentException("Document metadata exceeds the supported storage length.");
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            INSERT dbo.Documents (Id, SourceUrl, BlobName, OwnerId, ChunkCount, CreatedAt, IndexedAt,
                                  Status, StatusUpdatedAt, FailureCode)
            VALUES (@Id, @SourceUrl, @BlobName, @OwnerId, 0, @CreatedAt, NULL, N'Queued', @CreatedAt, NULL);
            """, connection);
        AddIdentity(command, document.Id, document.OwnerId);
        command.Parameters.Add("@SourceUrl", SqlDbType.NVarChar, 2048).Value = document.SourceUrl;
        command.Parameters.Add("@BlobName", SqlDbType.NVarChar, 2048).Value = document.BlobName;
        command.Parameters.Add("@CreatedAt", SqlDbType.DateTime2).Value = document.CreatedAt.UtcDateTime;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemovePendingAsync(string id, string ownerId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            DELETE dbo.Documents
            WHERE Id = @Id AND OwnerId = @OwnerId AND ChunkCount = 0
              AND DeletedAt IS NULL AND IndexedAt IS NULL;
            """, connection);
        AddIdentity(command, id, ownerId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<DocumentMetadata?> GetOwnedAsync(string id, string ownerId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            SELECT Id, SourceUrl, BlobName, OwnerId, ChunkCount, CreatedAt, IndexedAt,
                   DeletedAt, RecoverableUntil, DeletionProcessedAt, Status, StatusUpdatedAt, FailureCode
            FROM dbo.Documents
            WHERE Id = @Id AND OwnerId = @OwnerId;
            """, connection);
        AddIdentity(command, id, ownerId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new DocumentMetadata(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), Utc(reader, 5)!.Value, Utc(reader, 6), Utc(reader, 7), Utc(reader, 8), Utc(reader, 9),
            ParseStatus(reader.GetString(10)), Utc(reader, 11), reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    // An unknown persisted value is reported as Failed rather than guessed, so newer schema values
    // never appear as progress to callers.
    internal static DocumentStatus ParseStatus(string value) =>
        Enum.TryParse<DocumentStatus>(value, ignoreCase: false, out var status) && Enum.IsDefined(status)
            ? status
            : DocumentStatus.Failed;

    private static DateTimeOffset? Utc(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    public async Task MarkDeletedAsync(
        string id, string ownerId, DateTimeOffset deletedAt, DateTimeOffset recoverableUntil, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            UPDATE dbo.Documents
            SET DeletedAt = COALESCE(DeletedAt, @DeletedAt),
                RecoverableUntil = COALESCE(RecoverableUntil, @RecoverableUntil),
                DeletionProcessedAt = NULL
            WHERE Id = @Id AND OwnerId = @OwnerId;
            """, connection);
        AddIdentity(command, id, ownerId);
        command.Parameters.Add("@DeletedAt", SqlDbType.DateTime2).Value = deletedAt.UtcDateTime;
        command.Parameters.Add("@RecoverableUntil", SqlDbType.DateTime2).Value = recoverableUntil.UtcDateTime;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkDeletionProcessedAsync(
        string id, string ownerId, DateTimeOffset processedAt, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            UPDATE dbo.Documents
            SET DeletionProcessedAt = @ProcessedAt
            WHERE Id = @Id AND OwnerId = @OwnerId AND DeletedAt IS NOT NULL;
            """, connection);
        AddIdentity(command, id, ownerId);
        command.Parameters.Add("@ProcessedAt", SqlDbType.DateTime2).Value = processedAt.UtcDateTime;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RestoreAsync(string id, string ownerId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand("""
            UPDATE dbo.Documents
            SET DeletedAt = NULL, RecoverableUntil = NULL, DeletionProcessedAt = NULL
            WHERE Id = @Id AND OwnerId = @OwnerId
              AND DeletedAt IS NOT NULL AND RecoverableUntil > @Now;
            """, connection);
        AddIdentity(command, id, ownerId);
        command.Parameters.Add("@Now", SqlDbType.DateTime2).Value = now.UtcDateTime;
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Document is not within its recovery window.");
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        if (credential is not null)
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
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 64).Value = id;
        command.Parameters.Add("@OwnerId", SqlDbType.NVarChar, 128).Value = ownerId;
    }
}

public sealed record ExtractedTextContainerClient(Azure.Storage.Blobs.BlobContainerClient Client);
