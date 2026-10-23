using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Azure.AI.DocumentIntelligence;
using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DocIntake.Extractor;
using FluentAssertions;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntake.Tests;

public sealed class ExtractorFunctionTests
{
    [Fact]
    public void Functions_telemetry_initializes_with_compatible_application_insights_components()
    {
        using var host = new HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .ConfigureServices(services =>
            {
                services.AddApplicationInsightsTelemetryWorkerService();
                services.ConfigureFunctionsApplicationInsights();
            })
            .Build();

        var configuration = host.Services.GetRequiredService<TelemetryConfiguration>();

        configuration.TelemetryInitializers.Should().NotBeEmpty();
    }

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
        ILogger<ExtractDocument>? logger = null,
        IDocumentStatusPublisher? publisher = null,
        Azure.Core.Pipeline.HttpPipelineTransport? transport = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:BlobEndpoint"] = "https://test.blob.core.windows.net",
            ["Storage:ContainerName"] = "documents"
        }).Build();

        var blobOptions = new BlobClientOptions();
        var intelligenceOptions = new DocumentIntelligenceClientOptions();
        if (transport is not null)
        {
            blobOptions.Transport = transport;
            blobOptions.Retry.MaxRetries = 0;
            intelligenceOptions.Transport = transport;
            intelligenceOptions.Retry.MaxRetries = 0;
        }

        return new ExtractDocument(
            new BlobServiceClient(new Uri("https://test.blob.core.windows.net"), credential, blobOptions),
            new DocumentIntelligenceClient(
                new Uri("https://test.cognitiveservices.azure.com/"), credential, intelligenceOptions),
            sender,
            publisher ?? new RecordingStatusPublisher(),
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

    [Fact]
    public async Task Unreadable_document_reports_extracting_then_failed_unsupported_content_without_retry()
    {
        var documentId = Guid.NewGuid();
        var ownerId = Guid.NewGuid().ToString("D");
        using var handler = new FakeExtractorServices(ownerId, analyzeStatus: System.Net.HttpStatusCode.BadRequest);
        await using var serviceBus = new ServiceBusClient("docintake-test.servicebus.windows.net", new FakeTokenCredential());
        await using var sender = serviceBus.CreateSender("extracted-documents");
        var publisher = new RecordingStatusPublisher();
        var function = CreateFunction(new FakeTokenCredential(), sender, publisher: publisher,
            transport: new Azure.Core.Pipeline.HttpClientTransport(handler.HttpClient));

        var act = () => function.Run(BlobCreated(documentId), CancellationToken.None);

        await act.Should().NotThrowAsync("content rejected by Document Intelligence fails the same way on every retry");
        publisher.Events.Select(e => (e.DocumentId, e.OwnerId, e.Status, e.FailureCode)).Should().Equal(
            (documentId, ownerId, DocIntake.Shared.DocumentStatus.Extracting, (string?)null),
            (documentId, ownerId, DocIntake.Shared.DocumentStatus.Failed,
                DocIntake.Shared.DocumentFailureCodes.UnsupportedContent));
        publisher.Events.Select(e => e.MessageId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Transient_document_intelligence_failure_is_rethrown_after_reporting_extracting()
    {
        var documentId = Guid.NewGuid();
        using var handler = new FakeExtractorServices(Guid.NewGuid().ToString("D"),
            analyzeStatus: System.Net.HttpStatusCode.ServiceUnavailable);
        await using var serviceBus = new ServiceBusClient("docintake-test.servicebus.windows.net", new FakeTokenCredential());
        await using var sender = serviceBus.CreateSender("extracted-documents");
        var publisher = new RecordingStatusPublisher();
        var function = CreateFunction(new FakeTokenCredential(), sender, publisher: publisher,
            transport: new Azure.Core.Pipeline.HttpClientTransport(handler.HttpClient));

        var act = () => function.Run(BlobCreated(documentId), CancellationToken.None);

        await act.Should().ThrowAsync<Azure.RequestFailedException>();
        publisher.Events.Should().ContainSingle()
            .Which.Status.Should().Be(DocIntake.Shared.DocumentStatus.Extracting);
    }

    [Fact]
    public async Task Missing_owner_metadata_reports_no_status()
    {
        using var handler = new FakeExtractorServices(ownerId: null, analyzeStatus: System.Net.HttpStatusCode.BadRequest);
        await using var serviceBus = new ServiceBusClient("docintake-test.servicebus.windows.net", new FakeTokenCredential());
        await using var sender = serviceBus.CreateSender("extracted-documents");
        var publisher = new RecordingStatusPublisher();
        var function = CreateFunction(new FakeTokenCredential(), sender, publisher: publisher,
            transport: new Azure.Core.Pipeline.HttpClientTransport(handler.HttpClient));

        var act = () => function.Run(BlobCreated(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ownership*");
        publisher.Events.Should().BeEmpty();
    }

    private static EventGridEvent BlobCreated(Guid documentId) => new(
        "test-id",
        "Microsoft.Storage.BlobCreated",
        "1.0",
        BinaryData.FromObjectAsJson(new
        {
            url = $"https://test.blob.core.windows.net/documents/{documentId:D}/invoice.pdf"
        }));

    private sealed class RecordingStatusPublisher : IDocumentStatusPublisher
    {
        public List<DocIntake.Shared.DocumentStatusEvent> Events { get; } = [];

        public Task PublishAsync(DocIntake.Shared.DocumentStatusEvent statusEvent, CancellationToken ct)
        {
            Events.Add(statusEvent);
            return Task.CompletedTask;
        }
    }

    // Blob HEAD/GET and the Document Intelligence analyze call over a fake HTTP transport.
    private sealed class FakeExtractorServices(string? ownerId, System.Net.HttpStatusCode analyzeStatus)
        : HttpMessageHandler
    {
        private HttpClient? _client;

        public HttpClient HttpClient => _client ??= new HttpClient(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage response;
            if (request.RequestUri!.Host.StartsWith("test.blob", StringComparison.Ordinal))
            {
                response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x25, 0x50, 0x44, 0x46])
                };
                response.Headers.Add("x-ms-blob-type", "BlockBlob");
                if (ownerId is not null)
                {
                    response.Headers.Add("x-ms-meta-ownerId", ownerId);
                    response.Headers.Add("x-ms-meta-createdAt", DateTimeOffset.UtcNow.ToString("O"));
                }
            }
            else
            {
                response = new HttpResponseMessage(analyzeStatus)
                {
                    Content = new StringContent(
                        """{"error":{"code":"InvalidRequest","message":"The file is corrupted or format is unsupported."}}""",
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            }

            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

}
