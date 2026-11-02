using System.ClientModel;
using System.ClientModel.Primitives;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using DocIntake.Api.Services;
using DocIntake.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Xunit;

namespace DocIntake.Tests;

public class AzureSearchChunkRetrieverCallTests
{
    private static readonly float[] Vector = new float[SemanticSearchDefaults.EmbeddingDimensions];

    private static (AzureSearchChunkRetriever Retriever, Mock<SearchClient> Search, Func<SearchOptions?> Captured) Create(
        params SearchResult<SearchDocument>[] results)
    {
        SearchOptions? captured = null;
        var raw = new Mock<Response>().Object;
        var search = new Mock<SearchClient>();
        search.Setup(s => s.SearchAsync<SearchDocument>(null, It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string?, SearchOptions, CancellationToken>((_, options, _) => captured = options)
            .ReturnsAsync(Response.FromValue(
                SearchModelFactory.SearchResults(results, results.Length, null, null, raw), raw));
        return (new AzureSearchChunkRetriever(search.Object, NullLogger<AzureSearchChunkRetriever>.Instance),
            search, () => captured);
    }

    private static SearchResult<SearchDocument> Result(string id, string source, string? content, double score) =>
        SearchModelFactory.SearchResult(
            new SearchDocument { ["documentId"] = id, ["source"] = source, ["content"] = content }, score, null);

    [Fact]
    public async Task RetrieveAsync_QueriesWithOwnerFilterAndMapsResultsInOrder()
    {
        var (retriever, _, captured) = Create(
            Result("d1", "https://blob/a", "First", 0.9),
            Result("d2", "https://blob/b", "Second", 0.8));

        var chunks = await retriever.RetrieveAsync(Vector, "owner-1", CancellationToken.None);

        chunks.Should().Equal(
            new RetrievedChunk("d1", "https://blob/a", "First", 0.9),
            new RetrievedChunk("d2", "https://blob/b", "Second", 0.8));
        captured()!.Filter.Should().Be("ownerId eq 'owner-1' and isDeleted eq false");
    }

    [Fact]
    public async Task RetrieveAsync_SkipsResultsWithoutContent()
    {
        var (retriever, _, _) = Create(Result("d1", "a", null, 0.9), Result("d2", "b", "Kept", 0.8));

        var chunks = await retriever.RetrieveAsync(Vector, "owner", CancellationToken.None);

        chunks.Should().ContainSingle().Which.DocumentId.Should().Be("d2");
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsEmptyWhenSearchHasNoResults()
    {
        var (retriever, _, _) = Create();

        var chunks = await retriever.RetrieveAsync(Vector, "owner", CancellationToken.None);

        chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_RejectsWrongDimensionWithoutCallingSearch()
    {
        var (retriever, search, _) = Create();

        var act = () => retriever.RetrieveAsync(new float[3], "owner", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*3 dimensions*");
        search.Verify(s => s.SearchAsync<SearchDocument>(
            It.IsAny<string?>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RetrieveAsync_RejectsBlankOwner(string owner)
    {
        var (retriever, _, _) = Create();

        var act = () => retriever.RetrieveAsync(Vector, owner, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RetrieveAsync_PropagatesSearchFailureInsteadOfReturningEmpty()
    {
        var search = new Mock<SearchClient>();
        search.Setup(s => s.SearchAsync<SearchDocument>(null, It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Search unavailable"));
        var retriever = new AzureSearchChunkRetriever(search.Object, NullLogger<AzureSearchChunkRetriever>.Instance);

        var act = () => retriever.RetrieveAsync(Vector, "owner", CancellationToken.None);

        await act.Should().ThrowAsync<RequestFailedException>();
    }
}

public class AzureOpenAiAnswerGeneratorCallTests
{
    private static ClientResult<ChatCompletion> Completion(string text) =>
        ClientResult.FromValue(
            OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(text)),
            new Mock<PipelineResponse>().Object);

    [Fact]
    public async Task GenerateAsync_SendsGroundedPromptWithBoundedOptionsAndReturnsAnswer()
    {
        ChatCompletionOptions? options = null;
        IReadOnlyList<ChatMessage>? messages = null;
        var chat = new Mock<ChatClient>();
        chat.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatCompletionOptions, CancellationToken>(
                (m, o, _) => (messages, options) = (m.ToList(), o))
            .ReturnsAsync(Completion(" Answer [source1]. "));
        var generator = new AzureOpenAiAnswerGenerator(chat.Object, NullLogger<AzureOpenAiAnswerGenerator>.Instance);

        var answer = await generator.GenerateAsync(
            "What?", new RagContext("[source1]\nFact", ["https://blob/a"]), CancellationToken.None);

        answer.Should().Be("Answer [source1].");
        options!.Temperature.Should().Be(AzureOpenAiAnswerGenerator.Temperature);
        options.MaxOutputTokenCount.Should().Be(AzureOpenAiAnswerGenerator.MaxOutputTokens);
        messages!.Should().HaveCount(2);
        messages![1].Content[0].Text.Should().Contain("[source1]\nFact").And.Contain("Question: What?");
    }

    [Fact]
    public async Task GenerateAsync_RejectsEmptyModelOutput()
    {
        var chat = new Mock<ChatClient>();
        chat.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Completion("  "));
        var generator = new AzureOpenAiAnswerGenerator(chat.Object, NullLogger<AzureOpenAiAnswerGenerator>.Instance);

        var act = () => generator.GenerateAsync("What?", new RagContext("[source1]\nFact", ["a"]), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no answer text*");
    }

    [Fact]
    public async Task GenerateAsync_PropagatesChatFailure()
    {
        var chat = new Mock<ChatClient>();
        chat.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException("quota exceeded", new Mock<PipelineResponse>().Object));
        var generator = new AzureOpenAiAnswerGenerator(chat.Object, NullLogger<AzureOpenAiAnswerGenerator>.Instance);

        var act = () => generator.GenerateAsync("What?", new RagContext("[source1]\nFact", ["a"]), CancellationToken.None);

        await act.Should().ThrowAsync<ClientResultException>();
    }
}

public class AzureOpenAiQuestionEmbedderCallTests
{
    [Fact]
    public async Task EmbedAsync_ReturnsVectorFromEmbeddingClient()
    {
        var expected = Enumerable.Range(0, SemanticSearchDefaults.EmbeddingDimensions).Select(i => (float)i).ToArray();
        var embeddings = new Mock<EmbeddingClient>();
        embeddings.Setup(c => c.GenerateEmbeddingAsync(
                "What?", It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientResult.FromValue(
                OpenAIEmbeddingsModelFactory.OpenAIEmbedding(0, expected), new Mock<PipelineResponse>().Object));
        var embedder = new AzureOpenAiQuestionEmbedder(
            embeddings.Object, NullLogger<AzureOpenAiQuestionEmbedder>.Instance);

        var vector = await embedder.EmbedAsync("What?", CancellationToken.None);

        vector.ToArray().Should().Equal(expected);
    }

    [Fact]
    public async Task EmbedAsync_RejectsBlankQuestionWithoutCallingService()
    {
        var embeddings = new Mock<EmbeddingClient>(MockBehavior.Strict);
        var embedder = new AzureOpenAiQuestionEmbedder(
            embeddings.Object, NullLogger<AzureOpenAiQuestionEmbedder>.Instance);

        var act = () => embedder.EmbedAsync(" ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
