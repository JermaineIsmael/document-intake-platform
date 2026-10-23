using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Search.Documents.Indexes;
using DocIntake.Shared;
using DocIntake.Worker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

public sealed class IngestionWorkerTests : IAsyncDisposable
{
    private readonly ServiceBusClient _serviceBus = new("fake.servicebus.windows.net", new FakeTokenCredential());
    private readonly RecordingIndexer _indexer = new();
    private readonly RecordingReceiver _receiver = new();
    private readonly FakeMetadataRepository _metadata = new();
    private readonly IngestionWorker _worker;

    public IngestionWorkerTests()
    {
        _worker = new IngestionWorker(
            _serviceBus,
            new SearchIndexClient(new Uri("https://fake-search.search.windows.net"), new FakeTokenCredential()),
            _indexer,
            _metadata,
            Options.Create(new IngestionWorkerOptions()),
            NullLogger<IngestionWorker>.Instance);
    }

    [Fact]
    public async Task Successful_indexing_completes_the_message()
    {
        var document = new DocumentMessage("https://st.blob.core.windows.net/documents/a.pdf", Guid.NewGuid(), "text");

        await _worker.HandleMessageAsync(Args(ExtractorBody(document), deliveryCount: 1));

        _indexer.Received.Should().ContainSingle().Which.Should().Be(document);
        _receiver.Actions.Should().Equal("complete");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Failure_before_max_delivery_count_abandons_for_redelivery(int deliveryCount)
    {
        _indexer.Failure = new InvalidOperationException("search unavailable");

        await _worker.HandleMessageAsync(Args(ExtractorBody(ValidDocument()), deliveryCount));

        _receiver.Actions.Should().Equal("abandon");
    }

    [Fact]
    public async Task Failure_on_max_delivery_count_dead_letters_with_reason()
    {
        _indexer.Failure = new InvalidOperationException("search unavailable");

        await _worker.HandleMessageAsync(Args(ExtractorBody(ValidDocument()), deliveryCount: 5));

        _receiver.Actions.Should().Equal("dead-letter");
        _receiver.DeadLetterReason.Should().Be("IndexingFailed");
        _receiver.DeadLetterDescription.Should().Contain("search unavailable");
        _metadata.StatusUpdates.Should().ContainSingle().Which.Should().Be(
            (_indexer.Received[0].DocumentId.ToString("D"), DocumentStatus.Failed, DocumentFailureCodes.IndexingFailed));
    }

    [Fact]
    public async Task Failure_before_max_delivery_count_does_not_mark_failed()
    {
        _indexer.Failure = new InvalidOperationException("search unavailable");

        await _worker.HandleMessageAsync(Args(ExtractorBody(ValidDocument()), deliveryCount: 2));

        _metadata.StatusUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task Status_write_failure_still_dead_letters_the_message()
    {
        _indexer.Failure = new InvalidOperationException("search unavailable");
        _metadata.StatusUpdateException = new InvalidOperationException("sql unavailable");

        await _worker.HandleMessageAsync(Args(ExtractorBody(ValidDocument()), deliveryCount: 5));

        _receiver.Actions.Should().Equal("dead-letter");
    }

    [Fact]
    public async Task Invalid_message_with_identifiable_owner_marks_the_document_failed()
    {
        var documentId = Guid.NewGuid();
        var ownerId = Guid.NewGuid().ToString("D");
        var body = BinaryData.FromString(
            $$"""{"sourceUrl":"ftp://host/a.pdf","documentId":"{{documentId:D}}","ownerId":"{{ownerId}}","content":"x"}""");

        await _worker.HandleMessageAsync(Args(body, deliveryCount: 1));

        _receiver.DeadLetterReason.Should().Be("InvalidDocumentMessage");
        _metadata.StatusUpdates.Should().ContainSingle().Which.Should().Be(
            (documentId.ToString("D"), DocumentStatus.Failed, DocumentFailureCodes.InvalidMessage));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"sourceUrl":"https://st.blob.core.windows.net/documents/a.pdf","content":"x"}""")]
    [InlineData("""{"sourceUrl":"ftp://host/a.pdf","documentId":"6f8a3f1e-2b7c-4b8e-9a51-0d2c9e7b4a11","content":"x"}""")]
    public async Task Invalid_message_is_dead_lettered_immediately_without_indexing(string body)
    {
        await _worker.HandleMessageAsync(Args(BinaryData.FromString(body), deliveryCount: 1));

        _indexer.Received.Should().BeEmpty();
        _receiver.Actions.Should().Equal("dead-letter");
        _receiver.DeadLetterReason.Should().Be("InvalidDocumentMessage");
        _metadata.StatusUpdates.Should().BeEmpty("no trustworthy document and owner identity is available");
    }

    [Fact]
    public async Task Settlement_failure_is_logged_not_thrown()
    {
        _receiver.SettlementFailure = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost);

        var act = () => _worker.HandleMessageAsync(Args(ExtractorBody(ValidDocument()), deliveryCount: 1));

        await act.Should().NotThrowAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _worker.Dispose();
        await _serviceBus.DisposeAsync();
    }

    private static DocumentMessage ValidDocument() =>
        new("https://st.blob.core.windows.net/documents/a.pdf", Guid.NewGuid(), "text");

    // Same serialization the Sprint 2 extractor uses when it sends to the queue.
    private static BinaryData ExtractorBody(DocumentMessage message) =>
        BinaryData.FromObjectAsJson(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private ProcessMessageEventArgs Args(BinaryData body, int deliveryCount) =>
        new(
            ServiceBusModelFactory.ServiceBusReceivedMessage(body: body, messageId: "m1", deliveryCount: deliveryCount),
            _receiver,
            CancellationToken.None);

    private sealed class RecordingIndexer : IChunkIndexer
    {
        public List<DocumentMessage> Received { get; } = [];

        public Exception? Failure { get; set; }

        public Task IndexAsync(DocumentMessage message, CancellationToken ct)
        {
            Received.Add(message);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class RecordingReceiver : ServiceBusReceiver
    {
        public List<string> Actions { get; } = [];

        public string? DeadLetterReason { get; private set; }

        public string? DeadLetterDescription { get; private set; }

        public ServiceBusException? SettlementFailure { get; set; }

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) =>
            Record("complete");

        public override Task AbandonMessageAsync(
            ServiceBusReceivedMessage message,
            IDictionary<string, object>? propertiesToModify = null,
            CancellationToken cancellationToken = default) =>
            Record("abandon");

        public override Task DeadLetterMessageAsync(
            ServiceBusReceivedMessage message,
            string deadLetterReason,
            string? deadLetterErrorDescription = null,
            CancellationToken cancellationToken = default)
        {
            DeadLetterReason = deadLetterReason;
            DeadLetterDescription = deadLetterErrorDescription;
            return Record("dead-letter");
        }

        public override Task DeadLetterMessageAsync(
            ServiceBusReceivedMessage message,
            IDictionary<string, object>? propertiesToModify,
            string deadLetterReason,
            string? deadLetterErrorDescription = null,
            CancellationToken cancellationToken = default) =>
            DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription, cancellationToken);

        private Task Record(string action)
        {
            if (SettlementFailure is not null)
            {
                return Task.FromException(SettlementFailure);
            }

            Actions.Add(action);
            return Task.CompletedTask;
        }
    }
}
