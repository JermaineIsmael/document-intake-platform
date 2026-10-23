using DocIntake.Worker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

public sealed class DocumentRetentionWorkerTests
{
    [Fact]
    public async Task Sweep_expires_old_documents_and_retries_artifact_cleanup_through_repository_state()
    {
        var document = new RetentionDocument(
            Guid.NewGuid().ToString("D"), "id/source.pdf", "id/content.txt", 2);
        var repository = new RecordingMetadataRepository(document);
        var operations = new RecordingRetentionOperations();
        var now = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        var worker = new DocumentRetentionWorker(
            repository,
            operations,
            Options.Create(new RetentionOptions
            {
                RetentionDays = 90,
                BatchSize = 100,
                SweepInterval = TimeSpan.FromHours(1)
            }),
            new FixedTimeProvider(now),
            NullLogger<DocumentRetentionWorker>.Instance);

        await worker.RunSweepAsync(CancellationToken.None);

        repository.StaleTimeout.Should().Be(TimeSpan.FromHours(26));
        repository.MarkExpiredDays.Should().Be(90);
        operations.SoftDeleted.Should().ContainSingle().Which.Should().Be(document);
        repository.ProcessedIds.Should().ContainSingle().Which.Should().Be(document.Id);
        operations.Purged.Should().BeEmpty("the document is not yet past its recovery/purge date");
    }

    [Fact]
    public async Task Sweep_continues_retention_when_the_stale_status_sweep_fails()
    {
        var document = new RetentionDocument(Guid.NewGuid().ToString("D"), "id/source.pdf", "id/content.txt", 0);
        var repository = new RecordingMetadataRepository(document) { ThrowOnFailStale = true };
        var operations = new RecordingRetentionOperations();
        var worker = new DocumentRetentionWorker(repository, operations, Options.Create(new RetentionOptions()),
            new FixedTimeProvider(DateTimeOffset.UtcNow), NullLogger<DocumentRetentionWorker>.Instance);

        await worker.RunSweepAsync(CancellationToken.None);

        repository.MarkExpiredDays.Should().Be(90);
        operations.SoftDeleted.Should().ContainSingle();
    }

    [Fact]
    public void Stale_timeout_must_outlast_the_event_grid_retry_window()
    {
        new RetentionOptions().StaleProcessingTimeout.Should().BeGreaterThan(TimeSpan.FromHours(24));
        new RetentionOptions { StaleProcessingTimeout = TimeSpan.FromMinutes(5) }.IsValid().Should().BeFalse();
    }

    private sealed class RecordingMetadataRepository(RetentionDocument document) : IDocumentMetadataRepository
    {
        public int MarkExpiredDays { get; private set; }
        public List<string> ProcessedIds { get; } = [];

        public Task<bool> IsActiveAsync(string documentId, string ownerId, CancellationToken ct) =>
            Task.FromResult(true);

        public Task SaveAsync(string documentId, string sourceUrl, string sourceBlobName, string ownerId,
            DateTimeOffset createdAt, int chunkCount, CancellationToken ct) => Task.CompletedTask;

        public TimeSpan? StaleTimeout { get; private set; }
        public bool ThrowOnFailStale { get; init; }

        public Task<bool> TryUpdateStatusAsync(string documentId, string ownerId, DocIntake.Shared.DocumentStatus status,
            string? failureCode, CancellationToken ct) => Task.FromResult(true);

        public Task<int> FailStaleAsync(DateTimeOffset now, TimeSpan timeout, int batchSize, CancellationToken ct)
        {
            StaleTimeout = timeout;
            return ThrowOnFailStale ? throw new InvalidOperationException("sql down") : Task.FromResult(1);
        }
        public Task MarkExpiredAsync(DateTimeOffset now, int retentionDays, int batchSize, CancellationToken ct)
        {
            MarkExpiredDays = retentionDays;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetentionDocument>> GetPendingCleanupAsync(int batchSize, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RetentionDocument>>([document]);

        public Task MarkDeletionProcessedAsync(string documentId, DateTimeOffset processedAt, CancellationToken ct)
        {
            ProcessedIds.Add(documentId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetentionDocument>> GetPurgeReadyAsync(
            DateTimeOffset now, int batchSize, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RetentionDocument>>([]);

        public Task PurgeAsync(string documentId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingRetentionOperations : IDocumentRetentionOperations
    {
        public List<RetentionDocument> SoftDeleted { get; } = [];
        public List<RetentionDocument> Purged { get; } = [];

        public Task SoftDeleteAsync(RetentionDocument document, CancellationToken ct)
        {
            SoftDeleted.Add(document);
            return Task.CompletedTask;
        }

        public Task PurgeAsync(RetentionDocument document, CancellationToken ct)
        {
            Purged.Add(document);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
