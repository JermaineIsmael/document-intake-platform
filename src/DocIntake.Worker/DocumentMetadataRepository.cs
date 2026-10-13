using System.Data;
using Azure.Core;
using Microsoft.Data.SqlClient;

namespace DocIntake.Worker;

public interface IDocumentMetadataRepository
{
    Task SaveAsync(string documentId, string sourceUrl, int chunkCount, CancellationToken ct);
}

public sealed class DocumentMetadataRepository : IDocumentMetadataRepository
{
    public const int MaxIdLength = 64;
    public const int MaxSourceUrlLength = 2048;

    private static readonly string[] SqlScopes = ["https://database.windows.net/.default"];

    // Deliberately UPDATE-then-INSERT instead of MERGE. A MERGE without HOLDLOCK lets two concurrent
    // executions for the same new Id both take the "not matched" branch, and one fails with a primary
    // key violation - exactly the redelivery-during-processing race this worker must survive
    // (MaxConcurrentCalls > 1, or a lock lost mid-processing). MERGE also has a long history of
    // edge-case bugs. UPDLOCK + SERIALIZABLE takes a key-range lock on the Id even when no row exists,
    // so concurrent upserts for one document serialize and the second becomes an UPDATE.
    internal const string UpsertSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        UPDATE dbo.Documents WITH (UPDLOCK, SERIALIZABLE)
           SET SourceUrl = @SourceUrl,
               ChunkCount = @ChunkCount,
               IndexedAt = @IndexedAt
         WHERE Id = @Id;

        IF @@ROWCOUNT = 0
        BEGIN
            INSERT dbo.Documents (Id, SourceUrl, ChunkCount, IndexedAt)
            VALUES (@Id, @SourceUrl, @ChunkCount, @IndexedAt);
        END;

        COMMIT TRANSACTION;
        """;

    private readonly string _connectionString;
    private readonly TokenCredential? _credential;
    private readonly TimeProvider _timeProvider;

    /// <param name="connectionString">Server and database only; no password or Authentication keyword.</param>
    /// <param name="credential">
    /// Entra credential used for every new physical connection. Null is only for tests against a
    /// local SQL instance that authenticates through the connection string itself.
    /// </param>
    public DocumentMetadataRepository(string connectionString, TokenCredential? credential, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _credential = credential;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task SaveAsync(string documentId, string sourceUrl, int chunkCount, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);
        // Sized SqlParameters silently truncate longer values, so reject them instead.
        if (documentId.Length > MaxIdLength)
        {
            throw new ArgumentException($"Document ID exceeds {MaxIdLength} characters.", nameof(documentId));
        }

        if (sourceUrl.Length > MaxSourceUrlLength)
        {
            throw new ArgumentException($"Source URL exceeds {MaxSourceUrlLength} characters.", nameof(sourceUrl));
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new SqlCommand(UpsertSql, connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, MaxIdLength).Value = documentId;
        command.Parameters.Add("@SourceUrl", SqlDbType.NVarChar, MaxSourceUrlLength).Value = sourceUrl;
        command.Parameters.Add("@ChunkCount", SqlDbType.Int).Value = chunkCount;
        command.Parameters.Add("@IndexedAt", SqlDbType.DateTime2).Value = _timeProvider.GetUtcNow().UtcDateTime;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // AccessTokenCallback rather than "Authentication=Active Directory Default" or a static AccessToken:
    // - SqlClient 7.x moved the Active Directory authentication providers (and their Azure.Identity
    //   dependency) into the separate Microsoft.Data.SqlClient.Extensions.Azure package.
    // - The callback reuses the app's single DefaultAzureCredential (and its token cache), so local
    //   runs and managed identity follow the same credential chain as the other Azure clients.
    // - SqlClient invokes it for each new physical connection, so pooled connections get fresh tokens;
    //   a static AccessToken would expire and also fragments the pool per token value.
    private SqlConnection CreateConnection()
    {
        var connection = new SqlConnection(_connectionString);
        if (_credential is { } credential)
        {
            connection.AccessTokenCallback = async (_, cancellationToken) =>
            {
                var token = await credential
                    .GetTokenAsync(new TokenRequestContext(SqlScopes), cancellationToken)
                    .ConfigureAwait(false);
                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            };
        }

        return connection;
    }
}
