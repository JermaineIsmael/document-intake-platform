using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using DocIntake.Shared;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;

namespace DocIntake.Worker;

public interface IChunkIndexer
{
    Task IndexAsync(DocumentMessage message, CancellationToken ct);
}

public interface IExtractedTextReader
{
    Task<string> ReadAsync(string blobName, Guid documentId, string ownerId, CancellationToken ct);
}

public sealed class ChunkIndexerOptions
{
    public const string SectionName = "Indexing";

    // Azure OpenAI accepts up to 2048 inputs per request, but each request also counts against the
    // deployment's tokens-per-minute quota; ~100 x 800-character chunks is ~20K tokens, inside 30K TPM.
    public int EmbeddingBatchSize { get; set; } = 100;

    // AI Search accepts up to 1000 documents and 16 MB per indexing request; a 1536-float vector
    // serializes to roughly 20 KB, so 250 documents keeps each request around 5-6 MB.
    public int SearchUploadBatchSize { get; set; } = 250;

    public bool IsValid() =>
        EmbeddingBatchSize is > 0 and <= 2048 && SearchUploadBatchSize is > 0 and <= 1000;
}

public sealed class ChunkIndexer(
    EmbeddingClient embeddings,
    SearchClient search,
    IDocumentMetadataRepository metadata,
    IExtractedTextReader extractedTextReader,
    IOptions<ChunkIndexerOptions> options,
    ILogger<ChunkIndexer> logger) : IChunkIndexer
{
    private readonly ChunkIndexerOptions _options = options.Value;

    // Does the work or throws. Retry and dead-letter decisions belong to IngestionWorker.
    public async Task IndexAsync(DocumentMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var documentId = message.DocumentId.ToString("D");

        if (!await metadata.IsActiveAsync(documentId, message.OwnerId, ct).ConfigureAwait(false))
        {
            logger.LogInformation("Skipping deleted or unregistered document {DocumentId}", documentId);
            return;
        }

        // Rank-guarded: a redelivery after the document reached a terminal status leaves it unchanged.
        await metadata.TryUpdateStatusAsync(documentId, message.OwnerId, DocumentStatus.Processing, null, ct)
            .ConfigureAwait(false);

        var content = message.Content;
        if (content is null)
        {
            if (string.IsNullOrWhiteSpace(message.ExtractedTextBlobName))
            {
                throw new InvalidOperationException("Document message has no content or extracted-text reference.");
            }

            content = await extractedTextReader.ReadAsync(
                    message.ExtractedTextBlobName, message.DocumentId, message.OwnerId, ct)
                .ConfigureAwait(false);
        }

        var chunks = TextChunker.Split(content);
        if (chunks.Count == 0)
        {
            // Persist an explicit NoText result so callers can distinguish it from a processing failure.
            await metadata.SaveAsync(documentId, message.SourceUrl, message.SourceBlobName,
                message.OwnerId, message.CreatedAt, 0, ct).ConfigureAwait(false);
            logger.LogWarning("Document {DocumentId} has no extractable text; marked NoText", documentId);
            return;
        }

        var vectors = await EmbedAsync(chunks, ct).ConfigureAwait(false);

        var documents = new List<SearchDocument>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            documents.Add(new SearchDocument
            {
                ["id"] = $"{documentId}-{i}",
                ["documentId"] = documentId,
                ["ownerId"] = message.OwnerId,
                ["isDeleted"] = false,
                ["source"] = message.SourceUrl,
                ["content"] = chunks[i],
                ["contentVector"] = vectors[i]
            });
        }

        // Upload is an upsert keyed on the deterministic id "{documentId}-{chunkIndex}": a redelivered
        // message overwrites the same chunk documents in place instead of duplicating them. Document IDs
        // are per-upload GUIDs and the text is unchanged on redelivery, so the chunk set is identical.
        // ThrowOnAnyError makes partial per-document failures (reported in a 207 response) throw.
        var indexOptions = new IndexDocumentsOptions { ThrowOnAnyError = true };
        foreach (var batch in documents.Chunk(_options.SearchUploadBatchSize))
        {
            await search.UploadDocumentsAsync(batch, indexOptions, ct).ConfigureAwait(false);
        }

        await metadata.SaveAsync(documentId, message.SourceUrl, message.SourceBlobName,
            message.OwnerId, message.CreatedAt, chunks.Count, ct).ConfigureAwait(false);

        logger.LogInformation("Indexed {DocumentId} with {ChunkCount} chunks", documentId, chunks.Count);
    }

    private async Task<float[][]> EmbedAsync(IReadOnlyList<string> chunks, CancellationToken ct)
    {
        var vectors = new float[chunks.Count][];
        var offset = 0;
        foreach (var batch in chunks.Chunk(_options.EmbeddingBatchSize))
        {
            // No EmbeddingGenerationOptions.Dimensions: the model default (1536) must match the index.
            var result = await embeddings.GenerateEmbeddingsAsync(batch, cancellationToken: ct).ConfigureAwait(false);
            var collection = result.Value;
            if (collection.Count != batch.Length)
            {
                throw new InvalidOperationException(
                    $"Embedding service returned {collection.Count} vectors for {batch.Length} inputs.");
            }

            foreach (var embedding in collection)
            {
                if (embedding.Index < 0 || embedding.Index >= batch.Length)
                {
                    throw new InvalidOperationException($"Embedding index {embedding.Index} is out of range.");
                }

                var vector = embedding.ToFloats().ToArray();
                if (vector.Length != IndexSchema.EmbeddingDimensions)
                {
                    throw new InvalidOperationException(
                        $"Embedding has {vector.Length} dimensions; the index expects {IndexSchema.EmbeddingDimensions}.");
                }

                vectors[offset + embedding.Index] = vector;
            }

            offset += batch.Length;
        }

        if (Array.FindIndex(vectors, v => v is null) is var missing and >= 0)
        {
            throw new InvalidOperationException($"No embedding was returned for chunk {missing}.");
        }

        return vectors;
    }
}
