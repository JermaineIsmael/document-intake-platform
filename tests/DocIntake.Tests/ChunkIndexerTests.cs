using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using DocIntake.Shared;
using DocIntake.Worker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

// Real SDK clients over fake HTTP transports: the actual Azure OpenAI and AI Search request/response
// serialization runs, but nothing leaves the process.
public sealed class ChunkIndexerTests : IDisposable
{
    private const string SourceUrl = "https://stdocintakedev.blob.core.windows.net/documents/report.pdf";

    private readonly FakeAzureServices _services = new();
    private readonly FakeMetadataRepository _metadata = new();
    private readonly ChunkIndexer _indexer;

    public ChunkIndexerTests()
    {
        _indexer = CreateIndexer(_services, _metadata, embeddingBatchSize: 16, searchUploadBatchSize: 25);
    }

    [Fact]
    public async Task Document_with_content_uploads_one_search_document_per_chunk_with_ordered_ids()
    {
        // 800 + 59 * 700 characters => exactly 60 chunks with the default size 800 / overlap 100.
        var text = SyntheticText(800 + (59 * 700));
        var expectedChunks = TextChunker.Split(text);
        expectedChunks.Should().HaveCount(60);
        var message = new DocumentMessage(SourceUrl, Guid.NewGuid(), text);

        await _indexer.IndexAsync(message, CancellationToken.None);

        _services.EmbeddingRequests.Should().HaveCount(4, "60 chunks in batches of 16");
        _services.EmbeddingRequests.Sum(r => r.Count).Should().Be(60);
        _services.SearchUploadRequests.Should().Be(3, "60 documents in batches of 25");

        var documentId = message.DocumentId.ToString("D");
        _services.Index.Should().HaveCount(60);
        for (var i = 0; i < expectedChunks.Count; i++)
        {
            var uploaded = _services.Index[$"{documentId}-{i}"];
            uploaded["source"]!.GetValue<string>().Should().Be(SourceUrl);
            uploaded["content"]!.GetValue<string>().Should().Be(expectedChunks[i]);

            var vector = uploaded["contentVector"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
            vector.Should().HaveCount(IndexSchema.EmbeddingDimensions);
            // The fake returns embeddings out of order; each vector must still land on its own chunk.
            vector[0].Should().Be(FakeAzureServices.Fingerprint(expectedChunks[i]));
        }

        _metadata.Rows.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, (string, int)>(documentId, (SourceUrl, 60)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public async Task Document_without_content_calls_no_azure_service_and_records_no_text(string content)
    {
        var message = new DocumentMessage(SourceUrl, Guid.NewGuid(), content);

        await _indexer.IndexAsync(message, CancellationToken.None);

        _services.TotalRequests.Should().Be(0);
        _metadata.SaveCalls.Should().Be(1, "an explicit NoText result is persisted with zero chunks");
        _metadata.Rows[message.DocumentId.ToString("D")].ChunkCount.Should().Be(0);
        _metadata.StatusUpdates.Should().ContainSingle()
            .Which.Status.Should().Be(DocumentStatus.Processing);
    }

    [Fact]
    public async Task Indexing_marks_the_document_processing_before_any_azure_call()
    {
        var message = new DocumentMessage(SourceUrl, Guid.NewGuid(), SyntheticText(1000));

        await _indexer.IndexAsync(message, CancellationToken.None);

        _metadata.StatusUpdates.Should().ContainSingle().Which.Should()
            .Be((message.DocumentId.ToString("D"), DocumentStatus.Processing, (string?)null));
        _metadata.Rows[message.DocumentId.ToString("D")].ChunkCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Claim_check_message_reads_blob_text_and_indexes_owner_metadata()
    {
        var reader = new FakeExtractedTextReader { Text = SyntheticText(1000) };
        var indexer = CreateIndexer(_services, _metadata, extractedTextReader: reader);
        var documentId = Guid.NewGuid();
        var ownerId = Guid.NewGuid().ToString("D");
        var message = new DocumentMessage(
            SourceUrl, documentId, null, ownerId, DateTimeOffset.UtcNow,
            $"{documentId:D}/source.pdf", $"{documentId:D}/content.txt");

        await indexer.IndexAsync(message, CancellationToken.None);

        reader.LastBlobName.Should().Be($"{documentId:D}/content.txt");
        reader.LastOwnerId.Should().Be(ownerId);
        _services.Index.Values.Should().OnlyContain(doc =>
            doc["ownerId"]!.GetValue<string>() == ownerId
            && !doc["isDeleted"]!.GetValue<bool>()
            && doc["documentId"]!.GetValue<string>() == documentId.ToString("D"));
    }

    [Fact]
    public async Task Redelivered_message_is_processed_twice_without_duplicates()
    {
        var message = new DocumentMessage(SourceUrl, Guid.NewGuid(), SyntheticText(5000));
        var expectedCount = TextChunker.Split(message.Content!).Count;

        await _indexer.IndexAsync(message, CancellationToken.None);
        var redelivery = () => _indexer.IndexAsync(message, CancellationToken.None);

        await redelivery.Should().NotThrowAsync();
        _services.SearchUploadRequests.Should().BeGreaterThan(1, "both deliveries really uploaded");
        _services.Index.Should().HaveCount(expectedCount, "re-uploading the same ids overwrites in place");
        _services.Index.Keys.Should().OnlyContain(k => k.StartsWith(message.DocumentId.ToString("D")));
        _metadata.SaveCalls.Should().Be(2);
        _metadata.Rows.Should().ContainSingle()
            .Which.Value.Should().Be((SourceUrl, expectedCount));
    }

    [Fact]
    public async Task Partial_search_failure_throws_so_the_worker_can_retry()
    {
        _services.FailSearchKeysContaining = "-1";
        var message = new DocumentMessage(SourceUrl, Guid.NewGuid(), SyntheticText(3000));

        var act = () => _indexer.IndexAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        _metadata.SaveCalls.Should().Be(0, "metadata is only recorded after every chunk is indexed");
    }

    [Fact]
    public async Task Embedding_with_wrong_dimensions_throws_before_upload()
    {
        _services.EmbeddingDimensions = 3072;
        var message = new DocumentMessage(SourceUrl, Guid.NewGuid(), SyntheticText(1000));

        var act = () => _indexer.IndexAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*3072 dimensions*");
        _services.SearchUploadRequests.Should().Be(0);
    }

    public void Dispose() => _services.Dispose();

    internal static ChunkIndexer CreateIndexer(
        FakeAzureServices services,
        IDocumentMetadataRepository metadata,
        int embeddingBatchSize = 100,
        int searchUploadBatchSize = 250,
        FakeExtractedTextReader? extractedTextReader = null)
    {
        var openAi = new AzureOpenAIClient(
            new Uri("https://fake-openai.openai.azure.com/"),
            new FakeTokenCredential(),
            new AzureOpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(services.HttpClient),
                RetryPolicy = new ClientRetryPolicy(0)
            });

        var searchOptions = new SearchClientOptions { Transport = new HttpClientTransport(services.HttpClient) };
        searchOptions.Retry.MaxRetries = 0;
        var search = new SearchClient(
            new Uri("https://fake-search.search.windows.net"), IndexSchema.IndexName, new FakeTokenCredential(), searchOptions);

        return new ChunkIndexer(
            openAi.GetEmbeddingClient("text-embedding-3-small"),
            search,
            metadata,
            extractedTextReader ?? new FakeExtractedTextReader(),
            Options.Create(new ChunkIndexerOptions
            {
                EmbeddingBatchSize = embeddingBatchSize,
                SearchUploadBatchSize = searchUploadBatchSize
            }),
            NullLogger<ChunkIndexer>.Instance);
    }

    private static string SyntheticText(int length)
    {
        var random = new Random(42);
        var builder = new StringBuilder(length);
        while (builder.Length < length)
        {
            builder.Append((char)('a' + random.Next(26)));
            if (random.Next(7) == 0)
            {
                builder.Append(' ');
            }
        }

        return builder.ToString(0, length);
    }
}

internal sealed class FakeTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

internal sealed class FakeMetadataRepository : IDocumentMetadataRepository
{
    private readonly object _gate = new();

    public Dictionary<string, (string SourceUrl, int ChunkCount)> Rows { get; } = new();

    public int SaveCalls { get; private set; }

    public Task<bool> IsActiveAsync(string documentId, string ownerId, CancellationToken ct) =>
        Task.FromResult(true);

    // Same semantics as the SQL upsert: keyed on documentId, last write wins.
    public Task SaveAsync(string documentId, string sourceUrl, string sourceBlobName, string ownerId,
        DateTimeOffset createdAt, int chunkCount, CancellationToken ct)
    {
        lock (_gate)
        {
            SaveCalls++;
            Rows[documentId] = (sourceUrl, chunkCount);
        }

        return Task.CompletedTask;
    }

    public Task MarkExpiredAsync(DateTimeOffset now, int retentionDays, int batchSize, CancellationToken ct) =>
        Task.CompletedTask;

    public List<(string DocumentId, DocumentStatus Status, string? FailureCode)> StatusUpdates { get; } = [];

    public Exception? StatusUpdateException { get; set; }

    public Task<bool> TryUpdateStatusAsync(string documentId, string ownerId, DocumentStatus status,
        string? failureCode, CancellationToken ct)
    {
        if (StatusUpdateException is not null)
        {
            throw StatusUpdateException;
        }

        lock (_gate)
        {
            StatusUpdates.Add((documentId, status, failureCode));
        }

        return Task.FromResult(true);
    }

    public Task<int> FailStaleAsync(DateTimeOffset now, TimeSpan timeout, int batchSize, CancellationToken ct) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<RetentionDocument>> GetPendingCleanupAsync(int batchSize, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RetentionDocument>>([]);

    public Task MarkDeletionProcessedAsync(string documentId, DateTimeOffset processedAt, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<RetentionDocument>> GetPurgeReadyAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RetentionDocument>>([]);

    public Task PurgeAsync(string documentId, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeExtractedTextReader : IExtractedTextReader
{
    public string Text { get; set; } = "fake extracted text";
    public string? LastBlobName { get; private set; }
    public string? LastOwnerId { get; private set; }

    public Task<string> ReadAsync(string blobName, Guid documentId, string ownerId, CancellationToken ct) =>
        ReadCoreAsync(blobName, ownerId);

    private Task<string> ReadCoreAsync(string blobName, string ownerId)
    {
        LastBlobName = blobName;
        LastOwnerId = ownerId;
        return Task.FromResult(Text);
    }
}

// Emulates the two REST operations ChunkIndexer uses: Azure OpenAI embeddings and AI Search docs/index.
internal sealed class FakeAzureServices : HttpMessageHandler
{
    private readonly object _gate = new();

    public FakeAzureServices() => HttpClient = new HttpClient(this, disposeHandler: false);

    public HttpClient HttpClient { get; }

    public List<IReadOnlyList<string>> EmbeddingRequests { get; } = [];

    public int SearchUploadRequests { get; private set; }

    public int TotalRequests { get; private set; }

    public Dictionary<string, JsonObject> Index { get; } = new();

    public int EmbeddingDimensions { get; set; } = IndexSchema.EmbeddingDimensions;

    public string? FailSearchKeysContaining { get; set; }

    public static float Fingerprint(string text) => text.Aggregate(0, (sum, c) => (sum + c) % 1_000_000);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        lock (_gate)
        {
            TotalRequests++;
            if (path.EndsWith("/embeddings", StringComparison.Ordinal))
            {
                return Embeddings(JsonNode.Parse(body)!.AsObject());
            }

            if (path.Contains($"indexes('{IndexSchema.IndexName}')/docs/search.index", StringComparison.Ordinal))
            {
                return SearchIndex(JsonNode.Parse(body)!.AsObject());
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"Unexpected {request.Method} {path}") };
    }

    private HttpResponseMessage Embeddings(JsonObject request)
    {
        var inputs = request["input"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
        EmbeddingRequests.Add(inputs);
        var base64 = request["encoding_format"]?.GetValue<string>() == "base64";

        // Reverse order on purpose: callers must map results by "index", not by position.
        var data = new JsonArray();
        for (var i = inputs.Count - 1; i >= 0; i--)
        {
            var vector = new float[EmbeddingDimensions];
            vector[0] = Fingerprint(inputs[i]);
            vector[1] = i;
            JsonNode embedding = base64
                ? JsonValue.Create(Convert.ToBase64String(FloatsToBytes(vector)))!
                : new JsonArray(vector.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
            data.Add(new JsonObject { ["object"] = "embedding", ["index"] = i, ["embedding"] = embedding });
        }

        var response = new JsonObject
        {
            ["object"] = "list",
            ["data"] = data,
            ["model"] = "text-embedding-3-small",
            ["usage"] = new JsonObject { ["prompt_tokens"] = inputs.Count, ["total_tokens"] = inputs.Count }
        };
        return Json(HttpStatusCode.OK, response);
    }

    private HttpResponseMessage SearchIndex(JsonObject request)
    {
        SearchUploadRequests++;
        var results = new JsonArray();
        var anyFailed = false;
        foreach (var node in request["value"]!.AsArray())
        {
            var document = node!.AsObject();
            document["@search.action"]!.GetValue<string>().Should().Be("upload");
            var key = document["id"]!.GetValue<string>();
            var fail = FailSearchKeysContaining is { } marker && key.EndsWith(marker, StringComparison.Ordinal);
            anyFailed |= fail;
            if (!fail)
            {
                Index[key] = (JsonObject)document.DeepClone();
            }

            results.Add(new JsonObject
            {
                ["key"] = key,
                ["status"] = !fail,
                ["errorMessage"] = fail ? "Simulated indexing failure" : null,
                ["statusCode"] = fail ? 400 : 201
            });
        }

        // AI Search reports partial failure as 207 Multi-Status with per-document results.
        return Json(anyFailed ? HttpStatusCode.MultiStatus : HttpStatusCode.OK, new JsonObject { ["value"] = results });
    }

    private static byte[] FloatsToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(WireFormat), Encoding.UTF8, "application/json") };

    // Real services emit base64 '+' and '/' unescaped; the default encoder would write \u002B, which the
    // OpenAI SDK's base64 float decoder does not accept.
    private static readonly JsonSerializerOptions WireFormat = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HttpClient.Dispose();
        }

        base.Dispose(disposing);
    }
}
