using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace DocIntake.Worker;

public sealed class RetentionOptions
{
    public int RetentionDays { get; set; } = 90;
    public int BatchSize { get; set; } = 100;
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    // Longer than Event Grid's 24-hour retry TTL so a document still being retried is not failed early.
    public TimeSpan StaleProcessingTimeout { get; set; } = TimeSpan.FromHours(26);

    public bool IsValid() =>
        RetentionDays >= 1 && BatchSize is >= 1 and <= 1000
        && SweepInterval >= TimeSpan.FromMinutes(1)
        && StaleProcessingTimeout >= TimeSpan.FromHours(1);
}

public interface IDocumentRetentionOperations
{
    Task SoftDeleteAsync(RetentionDocument document, CancellationToken ct);
    Task PurgeAsync(RetentionDocument document, CancellationToken ct);
}

public sealed record OriginalDocumentsContainer(BlobContainerClient Client);

public sealed class BlobSearchRetentionOperations(
    SearchClient search,
    BlobContainerClient originalDocuments,
    BlobContainerClient extractedText) : IDocumentRetentionOperations
{
    public async Task SoftDeleteAsync(RetentionDocument document, CancellationToken ct)
    {
        await SetSearchDeletedAsync(document, isDeleted: true, ct).ConfigureAwait(false);
        await DeleteBlobIfNamedAsync(originalDocuments, document.SourceBlobName, ct).ConfigureAwait(false);
        await DeleteBlobIfNamedAsync(extractedText, document.ExtractedTextBlobName, ct).ConfigureAwait(false);
    }

    public async Task PurgeAsync(RetentionDocument document, CancellationToken ct)
    {
        if (document.ChunkCount > 0)
        {
            var ids = Enumerable.Range(0, document.ChunkCount)
                .Select(index => $"{document.Id}-{index}");
            await search.DeleteDocumentsAsync("id", ids, cancellationToken: ct).ConfigureAwait(false);
        }

        await DeleteBlobIfNamedAsync(originalDocuments, document.SourceBlobName, ct).ConfigureAwait(false);
        await DeleteBlobIfNamedAsync(extractedText, document.ExtractedTextBlobName, ct).ConfigureAwait(false);
    }

    private async Task SetSearchDeletedAsync(RetentionDocument document, bool isDeleted, CancellationToken ct)
    {
        if (document.ChunkCount <= 0)
        {
            return;
        }

        var values = Enumerable.Range(0, document.ChunkCount).Select(index => new SearchDocument
        {
            ["id"] = $"{document.Id}-{index}",
            ["isDeleted"] = isDeleted
        });
        await search.MergeDocumentsAsync(values, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task DeleteBlobIfNamedAsync(BlobContainerClient container, string blobName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(blobName))
        {
            await container.GetBlobClient(blobName)
                .DeleteIfExistsAsync(Azure.Storage.Blobs.Models.DeleteSnapshotsOption.IncludeSnapshots,
                    cancellationToken: ct).ConfigureAwait(false);
        }
    }
}

public sealed class DocumentRetentionWorker(
    IDocumentMetadataRepository metadata,
    IDocumentRetentionOperations operations,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<DocumentRetentionWorker> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSweepAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(_options.SweepInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task RunSweepAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            var timedOut = await metadata.FailStaleAsync(now, _options.StaleProcessingTimeout, _options.BatchSize, ct)
                .ConfigureAwait(false);
            if (timedOut > 0)
            {
                logger.LogWarning("Marked {Count} stalled documents as Failed (ProcessingTimedOut)", timedOut);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Stale document status sweep failed; it will be retried on the next sweep");
        }

        await metadata.MarkExpiredAsync(now, _options.RetentionDays, _options.BatchSize, ct).ConfigureAwait(false);

        var cleanup = await metadata.GetPendingCleanupAsync(_options.BatchSize, ct).ConfigureAwait(false);
        foreach (var document in cleanup)
        {
            try
            {
                await operations.SoftDeleteAsync(document, ct).ConfigureAwait(false);
                await metadata.MarkDeletionProcessedAsync(document.Id, now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Retention cleanup failed for document {DocumentId}; it will be retried", document.Id);
            }
        }

        var purge = await metadata.GetPurgeReadyAsync(now, _options.BatchSize, ct).ConfigureAwait(false);
        foreach (var document in purge)
        {
            try
            {
                await operations.PurgeAsync(document, ct).ConfigureAwait(false);
                await metadata.PurgeAsync(document.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Permanent retention purge failed for document {DocumentId}; it will be retried", document.Id);
            }
        }
    }
}
