namespace DocIntake.Api.Services;

public record AskResponse(string Answer, IReadOnlyList<string> Sources);

public sealed record RetrievedChunk(string DocumentId, string Source, string Content, double? Score);

// Sources[i] is the document cited as [source{i+1}] inside Text.
public sealed record RagContext(string Text, IReadOnlyList<string> Sources);

public interface IRagService
{
    Task<AskResponse?> AskAsync(string question, string ownerId, CancellationToken ct);
}

public interface IQuestionEmbedder
{
    Task<ReadOnlyMemory<float>> EmbedAsync(string question, CancellationToken ct);
}

public interface IDocumentChunkRetriever
{
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        ReadOnlyMemory<float> embedding,
        string ownerId,
        CancellationToken ct);
}

public interface IGroundedAnswerGenerator
{
    Task<string> GenerateAsync(
        string question,
        RagContext context,
        CancellationToken ct);
}

/// <summary>
/// Null implementation used while chat is explicitly disabled.
/// The endpoint checks the feature flag and returns 503 before invoking this service.
/// </summary>
public sealed class NullRagService : IRagService
{
    public Task<AskResponse?> AskAsync(string question, string ownerId, CancellationToken ct)
    {
        return Task.FromResult<AskResponse?>(null);
    }
}

/// <summary>
/// RAG orchestration for the /ask endpoint: embed the question, retrieve the caller's chunks,
/// build bounded context, and generate a grounded answer with its sources.
/// </summary>
public sealed class RagService(
    IQuestionEmbedder embeddings,
    IDocumentChunkRetriever retriever,
    IGroundedAnswerGenerator generator,
    ILogger<RagService> logger) : IRagService
{
    public async Task<AskResponse?> AskAsync(string question, string ownerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        logger.LogInformation("Question received (length {QuestionLength})", question.Length);

        try
        {
            var embedding = await embeddings.EmbedAsync(question, ct).ConfigureAwait(false);
            var chunks = await retriever.RetrieveAsync(embedding, ownerId, ct).ConfigureAwait(false);
            var context = RagContextBuilder.Build(chunks);
            if (context is null)
            {
                logger.LogInformation("No relevant document chunks were retrieved");
                return null;
            }

            var answer = await generator.GenerateAsync(question, context, ct).ConfigureAwait(false);
            logger.LogInformation("Generated answer from {SourceCount} sources", context.Sources.Count);
            return new AskResponse(answer, context.Sources);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in RAG query");
            throw;
        }
    }
}
