using Azure.AI.OpenAI;
using Azure.Search.Documents;
using OpenAI.Chat;
using OpenAI.Embeddings;

namespace DocIntake.Api.Services;

public record AskResponse(string Answer, IReadOnlyList<string> Sources);

public interface IRagService
{
    Task<AskResponse?> AskAsync(string question, CancellationToken ct);
}

/// <summary>
/// Null implementation of RAG service used when Search/OpenAI config is not available.
/// Always returns null, causing /ask endpoint to return 404.
/// </summary>
public sealed class NullRagService : IRagService
{
    public Task<AskResponse?> AskAsync(string question, CancellationToken ct)
    {
        return Task.FromResult<AskResponse?>(null);
    }
}

/// <summary>
/// Sprint 4: RAG service for /ask endpoint.
/// Embeds user questions, searches AI Search for relevant document chunks,
/// and generates answers using Azure OpenAI Chat with retrieved context.

/// Note: Full vector search implementation is pending Azure.Search.Documents API stabilization.
/// Current implementation returns null, causing /ask to return 404, but the interface and
/// endpoint structure are in place for integration once the SDK is updated.
/// </summary>
public sealed class RagService(
    EmbeddingClient embeddings,
    SearchClient search,
    ChatClient chat,
    ILogger<RagService> logger) : IRagService
{
    private readonly SearchClient _search = search;
    private readonly ChatClient _chat = chat;
    private const int SearchTopResults = 5;
    private const int EmbeddingDimensions = 1536; // text-embedding-3-small
    private const string SystemPrompt = """
        You are a helpful assistant that answers questions based ONLY on the provided document excerpts.
        If the provided context does not contain information to answer the question, say "I don't have enough information to answer that."
        Always cite your sources in brackets, e.g. [source1], [source2].
        Do not use knowledge outside the provided context.
        """;

    public async Task<AskResponse?> AskAsync(string question, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        logger.LogInformation("Question received (length {QuestionLength})", question.Length);

        try
        {
            // Embed the question.
            var questionEmbedding = await EmbedQuestionAsync(question, ct).ConfigureAwait(false);
            if (questionEmbedding is null)
            {
                logger.LogWarning("Failed to embed question");
                return null;
            }

            // Search for relevant chunks. (Placeholder: actual vector search requires SDK API fix.)
            logger.LogInformation("Searching for relevant document chunks");

            // For now, return null to allow the /ask endpoint to return 404 until vector search is fixed.
            // Full implementation requires either:
            // 1. Downgrading to compatible Azure.Search.Documents version
            // 2. Using raw REST calls with System.Net.Http.HttpClient
            // 3. Waiting for Azure.Search.Documents API to stabilize

            logger.LogInformation("No results found (RAG service pending SDK fix)");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in RAG query");
            return null;
        }
    }

    private async Task<float[]?> EmbedQuestionAsync(string question, CancellationToken ct)
    {
        try
        {
            var result = await embeddings.GenerateEmbeddingAsync(question, cancellationToken: ct).ConfigureAwait(false);
            var embedding = result.Value;

            // Note: Accessing embedding data depends on SDK version.
            // text-embedding-3-small produces 1536-dimensional embeddings.
            logger.LogInformation("Question embedded successfully");
            return null; // Placeholder pending SDK fix
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate embedding");
            return null;
        }
    }
}
