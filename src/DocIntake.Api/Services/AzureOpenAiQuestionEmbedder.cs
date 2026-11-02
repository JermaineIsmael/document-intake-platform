using DocIntake.Shared;
using OpenAI.Embeddings;

namespace DocIntake.Api.Services;

public sealed class AzureOpenAiQuestionEmbedder(
    EmbeddingClient embeddings,
    ILogger<AzureOpenAiQuestionEmbedder> logger) : IQuestionEmbedder
{
    public async Task<ReadOnlyMemory<float>> EmbedAsync(string question, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var result = await embeddings.GenerateEmbeddingAsync(question, cancellationToken: ct).ConfigureAwait(false);
        var vector = ExtractVector(result.Value);
        logger.LogInformation("Question embedded successfully with {EmbeddingDimensions} dimensions", vector.Length);
        return vector;
    }

    internal static float[] ExtractVector(OpenAIEmbedding embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        var vector = embedding.ToFloats().ToArray();
        if (vector.Length != SemanticSearchDefaults.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding has {vector.Length} dimensions; the index expects " +
                $"{SemanticSearchDefaults.EmbeddingDimensions}.");
        }

        return vector;
    }
}
