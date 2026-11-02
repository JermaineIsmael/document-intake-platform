using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using DocIntake.Shared;

namespace DocIntake.Api.Services;

public sealed class AzureSearchChunkRetriever(
    SearchClient search,
    ILogger<AzureSearchChunkRetriever> logger) : IDocumentChunkRetriever
{
    public const int TopResults = 5;

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        ReadOnlyMemory<float> embedding,
        string ownerId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (embedding.Length != SemanticSearchDefaults.EmbeddingDimensions)
        {
            throw new ArgumentException(
                $"Embedding has {embedding.Length} dimensions; the index expects {SemanticSearchDefaults.EmbeddingDimensions}.",
                nameof(embedding));
        }

        var response = await search.SearchAsync<SearchDocument>(null, BuildOptions(embedding, ownerId), ct)
            .ConfigureAwait(false);

        var chunks = new List<RetrievedChunk>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(ct).ConfigureAwait(false))
        {
            if (ToChunk(result) is { } chunk)
            {
                chunks.Add(chunk);
            }
        }

        logger.LogInformation("Retrieved {ChunkCount} chunks", chunks.Count);
        return chunks;
    }

    // The owner and deletion filters run inside Search, never as an in-memory post-filter.
    internal static SearchOptions BuildOptions(ReadOnlyMemory<float> embedding, string ownerId)
    {
        var options = new SearchOptions
        {
            Filter = SearchFilter.Create($"ownerId eq {ownerId} and isDeleted eq false"),
            Size = TopResults,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(embedding)
                    {
                        KNearestNeighborsCount = TopResults,
                        Fields = { "contentVector" }
                    }
                }
            }
        };
        options.Select.Add("documentId");
        options.Select.Add("source");
        options.Select.Add("content");
        return options;
    }

    internal static RetrievedChunk? ToChunk(SearchResult<SearchDocument> result)
    {
        var document = result.Document;
        if (!document.TryGetValue("content", out var content) || content is not string text
            || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new RetrievedChunk(
            document.TryGetValue("documentId", out var id) ? id?.ToString() ?? string.Empty : string.Empty,
            document.TryGetValue("source", out var source) ? source?.ToString() ?? string.Empty : string.Empty,
            text,
            result.Score);
    }
}
