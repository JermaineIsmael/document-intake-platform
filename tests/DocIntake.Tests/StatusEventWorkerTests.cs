using Azure.Messaging.ServiceBus;
using DocIntake.Shared;
using DocIntake.Worker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

public sealed class StatusEventWorkerTests : IAsyncDisposable
{
    private readonly ServiceBusClient _serviceBus = new("fake.servicebus.windows.net", new FakeTokenCredential());
    private readonly FakeMetadataRepository _metadata = new();
    private readonly SettlementRecorder _receiver = new();
    private readonly StatusEventWorker _worker;

    public StatusEventWorkerTests()
    {
        _worker = new StatusEventWorker(_serviceBus, _metadata,
            Options.Create(new StatusEventWorkerOptions()), NullLogger<StatusEventWorker>.Instance);
    }

    [Fact]
    public async Task Extracting_event_updates_status_and_completes()
    {
        var statusEvent = Event(DocumentStatus.Extracting, null);

        await _worker.HandleMessageAsync(Args(statusEvent, deliveryCount: 1));

        _metadata.StatusUpdates.Should().ContainSingle().Which.Should()
            .Be((statusEvent.DocumentId.ToString("D"), DocumentStatus.Extracting, (string?)null));
        _receiver.Actions.Should().Equal("complete");
    }

    [Fact]
    public async Task Failed_event_with_known_code_is_applied()
    {
        await _worker.HandleMessageAsync(
            Args(Event(DocumentStatus.Failed, DocumentFailureCodes.UnsupportedContent), deliveryCount: 1));

        _metadata.StatusUpdates.Should().ContainSingle()
            .Which.FailureCode.Should().Be(DocumentFailureCodes.UnsupportedContent);
        _receiver.Actions.Should().Equal("complete");
    }

    [Theory]
    [InlineData(DocumentStatus.Indexed, null)]
    [InlineData(DocumentStatus.Processing, null)]
    [InlineData(DocumentStatus.Failed, "SomethingElse")]
    [InlineData(DocumentStatus.Failed, null)]
    [InlineData(DocumentStatus.Extracting, "ExtractionFailed")]
    public async Task Events_the_extractor_may_not_send_are_dead_lettered(DocumentStatus status, string? code)
    {
        await _worker.HandleMessageAsync(Args(Event(status, code), deliveryCount: 1));

        _metadata.StatusUpdates.Should().BeEmpty();
        _receiver.Actions.Should().Equal("dead-letter");
    }

    [Fact]
    public async Task Malformed_body_is_dead_lettered()
    {
        await _worker.HandleMessageAsync(new ProcessMessageEventArgs(
            ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromString("nope"), deliveryCount: 1),
            _receiver, CancellationToken.None));

        _receiver.Actions.Should().Equal("dead-letter");
    }

    [Fact]
    public async Task Sql_failure_abandons_until_the_final_delivery()
    {
        _metadata.StatusUpdateException = new InvalidOperationException("sql unavailable");

        await _worker.HandleMessageAsync(Args(Event(DocumentStatus.Extracting, null), deliveryCount: 1));
        await _worker.HandleMessageAsync(Args(Event(DocumentStatus.Extracting, null), deliveryCount: 10));

        _receiver.Actions.Should().Equal("abandon", "dead-letter");
    }

    public async ValueTask DisposeAsync()
    {
        _worker.Dispose();
        await _serviceBus.DisposeAsync();
    }

    private static DocumentStatusEvent Event(DocumentStatus status, string? code) =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), status, code, DateTimeOffset.UtcNow);

    private ProcessMessageEventArgs Args(DocumentStatusEvent statusEvent, int deliveryCount) =>
        new(
            ServiceBusModelFactory.ServiceBusReceivedMessage(
                BinaryData.FromObjectAsJson(statusEvent,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                messageId: statusEvent.MessageId,
                deliveryCount: deliveryCount),
            _receiver,
            CancellationToken.None);

    private sealed class SettlementRecorder : ServiceBusReceiver
    {
        public List<string> Actions { get; } = [];

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) =>
            Record("complete");

        public override Task AbandonMessageAsync(ServiceBusReceivedMessage message,
            IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default) =>
            Record("abandon");

        public override Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, string deadLetterReason,
            string? deadLetterErrorDescription = null, CancellationToken cancellationToken = default) =>
            Record("dead-letter");

        private Task Record(string action)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }
}
