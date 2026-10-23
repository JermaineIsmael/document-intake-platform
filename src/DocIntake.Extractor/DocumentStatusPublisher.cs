using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DocIntake.Shared;

namespace DocIntake.Extractor;

public interface IDocumentStatusPublisher
{
    Task PublishAsync(DocumentStatusEvent statusEvent, CancellationToken ct);
}

// The Extractor is not VNet-integrated and cannot reach the private SQL endpoint, so it reports lifecycle
// changes on a Service Bus queue that the Worker applies to SQL.
public sealed class ServiceBusDocumentStatusPublisher(ServiceBusSender statusSender) : IDocumentStatusPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task PublishAsync(DocumentStatusEvent statusEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(statusEvent, JsonOptions))
        {
            // Distinct per status so queue duplicate detection drops only true retries of the same event.
            MessageId = statusEvent.MessageId,
            ContentType = "application/json"
        };
        return statusSender.SendMessageAsync(message, ct);
    }
}
