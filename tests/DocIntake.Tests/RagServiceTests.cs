using Moq;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Xunit;
using FluentAssertions;
using Azure.Search.Documents;
using DocIntake.Api.Services;
using Microsoft.Extensions.Logging;

namespace DocIntake.Tests;

/// <summary>
/// Tests for RagService. Note: These are simplified tests since RagService uses
/// internal types and Azure SDK types that are difficult to mock comprehensively.
/// Full integration tests should be run against deployed Azure resources.
/// </summary>
public class RagServiceTests
{
    private readonly Mock<EmbeddingClient> _embeddingClientMock = new(MockBehavior.Strict);
    private readonly Mock<SearchClient> _searchClientMock = new(MockBehavior.Strict);
    private readonly Mock<ChatClient> _chatClientMock = new(MockBehavior.Strict);
    private readonly Mock<ILogger<RagService>> _loggerMock = new();

    [Fact]
    public async Task AskAsync_NullQuestion_ThrowsArgumentException()
    {
        // Arrange
        var ragService = new RagService(_embeddingClientMock.Object, _searchClientMock.Object, _chatClientMock.Object, _loggerMock.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => ragService.AskAsync(null!, "owner", CancellationToken.None));
    }

    [Fact]
    public async Task AskAsync_EmptyQuestion_ThrowsArgumentException()
    {
        // Arrange
        var ragService = new RagService(_embeddingClientMock.Object, _searchClientMock.Object, _chatClientMock.Object, _loggerMock.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => ragService.AskAsync("", "owner", CancellationToken.None));
    }

    [Fact]
    public async Task AskAsync_WhitespaceQuestion_ThrowsArgumentException()
    {
        // Arrange
        var ragService = new RagService(_embeddingClientMock.Object, _searchClientMock.Object, _chatClientMock.Object, _loggerMock.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => ragService.AskAsync("   ", "owner", CancellationToken.None));
    }

    [Fact]
    public void RagService_ImplementsIRagService()
    {
        // Verify the interface contract is implemented
        var ragService = new RagService(_embeddingClientMock.Object, _searchClientMock.Object, _chatClientMock.Object, _loggerMock.Object);
        ragService.Should().BeAssignableTo<IRagService>();
    }
}
