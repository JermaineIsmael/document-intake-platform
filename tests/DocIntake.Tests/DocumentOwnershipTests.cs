using DocIntake.Api.Services;
using FluentAssertions;

namespace DocIntake.Tests;

public sealed class DocumentOwnershipTests
{
    [Fact]
    public async Task Metadata_is_visible_only_to_the_uploader()
    {
        var store = new InMemoryDocumentMetadataStore();
        var documentId = Guid.NewGuid().ToString("D");
        var ownerId = Guid.NewGuid().ToString("D");
        var document = new DocumentMetadata(
            documentId, "https://storage.example/documents/a.pdf", $"{documentId}/a.pdf",
            ownerId, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null);
        await store.RegisterAsync(document, CancellationToken.None);

        (await store.GetOwnedAsync(documentId, ownerId, CancellationToken.None))
            .Should().Be(document with { StatusUpdatedAt = document.CreatedAt });
        (await store.GetOwnedAsync(documentId, Guid.NewGuid().ToString("D"), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Deleted_metadata_can_be_restored_only_before_recovery_expiry()
    {
        var store = new InMemoryDocumentMetadataStore();
        var documentId = Guid.NewGuid().ToString("D");
        var ownerId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        await store.RegisterAsync(new DocumentMetadata(
            documentId, "https://storage.example/documents/a.pdf", $"{documentId}/a.pdf",
            ownerId, 1, now.AddDays(-1), now.AddDays(-1), null, null, null), CancellationToken.None);

        await store.MarkDeletedAsync(documentId, ownerId, now, now.AddDays(30), CancellationToken.None);
        await store.MarkDeletionProcessedAsync(documentId, ownerId, now, CancellationToken.None);
        await store.RestoreAsync(documentId, ownerId, now.AddDays(29), CancellationToken.None);

        var restored = await store.GetOwnedAsync(documentId, ownerId, CancellationToken.None);
        restored!.DeletedAt.Should().BeNull();
        restored.RecoverableUntil.Should().BeNull();
    }

    [Fact]
    public async Task Deleted_metadata_cannot_be_restored_after_recovery_expiry()
    {
        var store = new InMemoryDocumentMetadataStore();
        var documentId = Guid.NewGuid().ToString("D");
        var ownerId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        await store.RegisterAsync(new DocumentMetadata(
            documentId, "https://storage.example/documents/a.pdf", $"{documentId}/a.pdf",
            ownerId, 1, now, now, null, null, null), CancellationToken.None);
        await store.MarkDeletedAsync(documentId, ownerId, now, now.AddDays(30), CancellationToken.None);

        var act = () => store.RestoreAsync(documentId, ownerId, now.AddDays(31), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
