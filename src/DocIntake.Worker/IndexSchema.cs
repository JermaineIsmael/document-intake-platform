using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace DocIntake.Worker;

public static class IndexSchema
{
    public const string IndexName = "document-chunks";
    public const string VectorProfileName = "content-vector-profile";
    public const string HnswConfigurationName = "content-hnsw";

    // text-embedding-3-small returns 1536 dimensions by default (also its maximum). The model accepts
    // a smaller "dimensions" request option; the embedding caller must not set it unless this changes too.
    public const int EmbeddingDimensions = 1536;

    public static SearchIndex Build() => new(IndexName)
    {
        Fields =
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
            new SimpleField("documentId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("ownerId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("isDeleted", SearchFieldDataType.Boolean) { IsFilterable = true },
            new SearchableField("content"),
            new SimpleField("source", SearchFieldDataType.String) { IsFilterable = true },
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = EmbeddingDimensions,
                VectorSearchProfileName = VectorProfileName
            }
        },
        VectorSearch = new VectorSearch
        {
            Algorithms = { new HnswAlgorithmConfiguration(HnswConfigurationName) },
            Profiles = { new VectorSearchProfile(VectorProfileName, HnswConfigurationName) }
        }
    };

    public static async Task EnsureAsync(SearchIndexClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        await client.CreateOrUpdateIndexAsync(Build(), cancellationToken: ct).ConfigureAwait(false);
    }
}
