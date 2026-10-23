using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace DocIntake.Api.Services;

public interface IDocumentIndexLifecycle
{
    Task SetDeletedAsync(string documentId, int chunkCount, bool isDeleted, CancellationToken ct);
}

public sealed class SearchDocumentIndexLifecycle(SearchClient search) : IDocumentIndexLifecycle
{
    public async Task SetDeletedAsync(string documentId, int chunkCount, bool isDeleted, CancellationToken ct)
    {
        if (chunkCount <= 0)
        {
            return;
        }

        var documents = Enumerable.Range(0, chunkCount).Select(index => new SearchDocument
        {
            ["id"] = $"{documentId}-{index}",
            ["isDeleted"] = isDeleted
        });
        await search.MergeDocumentsAsync(documents, cancellationToken: ct).ConfigureAwait(false);
    }
}

public sealed class NoopDocumentIndexLifecycle : IDocumentIndexLifecycle
{
    public Task SetDeletedAsync(string documentId, int chunkCount, bool isDeleted, CancellationToken ct) =>
        Task.CompletedTask;
}

public sealed class UnavailableDocumentIndexLifecycle(string reason) : IDocumentIndexLifecycle
{
    public Task SetDeletedAsync(string documentId, int chunkCount, bool isDeleted, CancellationToken ct) =>
        Task.FromException(new InvalidOperationException(reason));
}

public sealed class DocumentLifecycleService(
    IDocumentMetadataStore metadata,
    DocumentStore blobs,
    IDocumentIndexLifecycle index,
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptions<DocumentLifecycleOptions> options)
{
    private readonly DocumentLifecycleOptions _options = options.Value;

    public Task<DocumentMetadata?> GetOwnedAsync(string id, string ownerId, CancellationToken ct) =>
        metadata.GetOwnedAsync(id, ownerId, ct);

    public Task RegisterAsync(DocumentMetadata document, CancellationToken ct) =>
        metadata.RegisterAsync(document, ct);

    public Task CancelPendingUploadAsync(string id, string ownerId, CancellationToken ct) =>
        metadata.RemovePendingAsync(id, ownerId, ct);

    public async Task<bool> DeleteAsync(string id, string ownerId, CancellationToken ct)
    {
        var document = await metadata.GetOwnedAsync(id, ownerId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        await metadata.MarkDeletedAsync(id, ownerId, now, now.AddDays(_options.RecoveryDays), ct).ConfigureAwait(false);
        await index.SetDeletedAsync(id, document.ChunkCount, isDeleted: true, ct).ConfigureAwait(false);
        await blobs.DeleteAsync(document.BlobName, Guid.Parse(id), ct).ConfigureAwait(false);
        await metadata.MarkDeletionProcessedAsync(id, ownerId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RestoreAsync(string id, string ownerId, CancellationToken ct)
    {
        var document = await metadata.GetOwnedAsync(id, ownerId, ct).ConfigureAwait(false);
        if (document?.RecoverableUntil is not { } recoverableUntil || recoverableUntil <= timeProvider.GetUtcNow())
        {
            return false;
        }

        await blobs.RestoreAsync(document.BlobName, Guid.Parse(id), ct).ConfigureAwait(false);
        await index.SetDeletedAsync(id, document.ChunkCount, isDeleted: false, ct).ConfigureAwait(false);
        await metadata.RestoreAsync(id, ownerId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        return true;
    }
}

public sealed class DocumentLifecycleOptions
{
    public int RecoveryDays { get; set; } = 30;
}
