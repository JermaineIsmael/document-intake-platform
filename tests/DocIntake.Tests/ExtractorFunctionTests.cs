using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Azure.AI.DocumentIntelligence;
using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DocIntake.Extractor;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntake.Tests;

public sealed class ExtractorFunctionTests
{
    [Fact]
    public async Task Non_blob_created_events_are_ignored()
    {
        var credential = new DefaultAzureCredential();
        await using var serviceBus = new ServiceBusClient("docintake-test.servicebus.windows.net", credential);
        await using var sender = serviceBus.CreateSender("extracted-documents");
        var function = CreateFunction(credential, sender);
        var evt = new EventGridEvent(
            "test-id", "Microsoft.Storage.BlobDeleted", "1.0", BinaryData.FromString("{}"));

        var act = () => function.Run(evt, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"url":"not a URL"}""")]
    [InlineData("""{"url":"https://other.blob.core.windows.net/documents/id/file.pdf"}""")]
    [InlineData("""{"url":"https://test.blob.core.windows.net/other-container/id/file.pdf"}""")]
    [InlineData("""{"url":"http://test.blob.core.windows.net/documents/id/file.pdf"}""")]
    public async Task Unrecognized_or_out_of_scope_blob_events_fail_before_cloud_access(string json)
    {
        var credential = new DefaultAzureCredential();
        await using var serviceBus = new ServiceBusClient("docintake-test.servicebus.windows.net", credential);
        await using var sender = serviceBus.CreateSender("extracted-documents");
        var function = CreateFunction(credential, sender);
        var evt = new EventGridEvent(
            "test-id", "Microsoft.Storage.BlobCreated", "1.0", BinaryData.FromString(json));

        var act = () => function.Run(evt, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Shared_queue_contract_serializes_for_future_consumers()
    {
        var message = new DocIntake.Shared.DocumentMessage(
            "https://storage.example/documents/b07dd851-252c-46a5-8d67-df9744b5f437/invoice.pdf",
            Guid.Parse("b07dd851-252c-46a5-8d67-df9744b5f437"),
            "invoice text");

        var json = BinaryData.FromObjectAsJson(
            message, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            .ToString();

        json.Should().Contain("\"documentId\"")
            .And.Contain("\"sourceUrl\"")
            .And.Contain("\"content\":\"invoice text\"");
    }

    [Fact]
    public async Task Blob_download_failure_is_logged_and_rethrown_for_event_grid_retry()
    {
        var documentId = Guid.NewGuid();
        var credential = new FailingTokenCredential();
        await using var serviceBus = new ServiceBusClient("docintake-test.servicebus.windows.net", credential);
        await using var sender = serviceBus.CreateSender("extracted-documents");
        var logger = new RecordingLogger<ExtractDocument>();
        var function = CreateFunction(credential, sender, logger);
        var evt = new EventGridEvent(
            "test-id",
            "Microsoft.Storage.BlobCreated",
            "1.0",
            BinaryData.FromObjectAsJson(new
            {
                url = $"https://test.blob.core.windows.net/documents/{documentId:D}/invoice.pdf"
            }));

        var act = () => function.Run(evt, CancellationToken.None);

        await act.Should().ThrowAsync<CredentialUnavailableException>();
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Error
            && entry.Exception is CredentialUnavailableException
            && entry.Message.Contains(documentId.ToString(), StringComparison.Ordinal)
            && entry.Message.Contains("https://test.blob.core.windows.net/documents/", StringComparison.Ordinal));
    }

    private static ExtractDocument CreateFunction(
        TokenCredential credential,
        ServiceBusSender sender,
        ILogger<ExtractDocument>? logger = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:BlobEndpoint"] = "https://test.blob.core.windows.net",
            ["Storage:ContainerName"] = "documents"
        }).Build();

        return new ExtractDocument(
            new BlobServiceClient(new Uri("https://test.blob.core.windows.net"), credential),
            new DocumentIntelligenceClient(
                new Uri("https://test.cognitiveservices.azure.com/"), credential),
            sender,
            configuration,
            logger ?? NullLogger<ExtractDocument>.Instance);
    }

    private sealed class FailingTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new CredentialUnavailableException("Test credential intentionally unavailable.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
            => ValueTask.FromException<AccessToken>(
                new CredentialUnavailableException("Test credential intentionally unavailable."));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

}
