using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DocIntake.Shared;
using Microsoft.Extensions.Options;

namespace DocIntake.Worker;

public sealed class StatusEventWorkerOptions
{
    public const string SectionName = "ServiceBus";

    public string StatusQueueName { get; set; } = "document-status";

    // Must equal the document-status queue's maxDeliveryCount in infra/main.bicep.
    public int StatusMaxDeliveryCount { get; set; } = 10;

    public bool IsValid() => !string.IsNullOrWhiteSpace(StatusQueueName) && StatusMaxDeliveryCount >= 1;
}

// Applies status events from the Extractor, which cannot reach the network-isolated SQL database itself.
public sealed class StatusEventWorker(
    ServiceBusClient serviceBusClient,
    IDocumentMetadataRepository metadata,
    IOptions<StatusEventWorkerOptions> options,
    ILogger<StatusEventWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly StatusEventWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = serviceBusClient.CreateProcessor(_options.StatusQueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            MaxConcurrentCalls = 4
        });
        processor.ProcessMessageAsync += HandleMessageAsync;
        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus status processor error from {ErrorSource} on {EntityPath}",
                args.ErrorSource, args.EntityPath);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        if (!TryParse(message.Body, out var statusEvent, out var error))
        {
            logger.LogError("Status message {MessageId} is invalid: {Reason}", message.MessageId, error);
            await args.DeadLetterMessageAsync(message, "InvalidStatusEvent", error, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var documentId = statusEvent.DocumentId.ToString("D");
        try
        {
            var applied = await metadata.TryUpdateStatusAsync(documentId, statusEvent.OwnerId, statusEvent.Status,
                statusEvent.FailureCode, args.CancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                // Out-of-order, duplicate, deleted, or unknown documents: the transition guard rejected it.
                logger.LogInformation("Status {Status} for {DocumentId} was not applied", statusEvent.Status, documentId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !args.CancellationToken.IsCancellationRequested)
        {
            if (message.DeliveryCount >= _options.StatusMaxDeliveryCount)
            {
                logger.LogError(ex, "Status update for {DocumentId} failed on final delivery; dead-lettering", documentId);
                await args.DeadLetterMessageAsync(message, "StatusUpdateFailed",
                    ex.GetType().FullName ?? "Exception", CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning(ex, "Status update for {DocumentId} failed; abandoning for retry", documentId);
                await args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        await args.CompleteMessageAsync(message, CancellationToken.None).ConfigureAwait(false);
    }

    // The Extractor may only report that extraction started or failed; Processing and terminal success
    // statuses are owned by the Worker itself.
    internal static bool TryParse(BinaryData body, out DocumentStatusEvent statusEvent, out string error)
    {
        statusEvent = null!;
        DocumentStatusEvent? parsed;
        try
        {
            parsed = body.ToObjectFromJson<DocumentStatusEvent>(JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            error = "Body is not valid DocumentStatusEvent JSON.";
            return false;
        }

        if (parsed is null || parsed.DocumentId == Guid.Empty || !Guid.TryParse(parsed.OwnerId, out _))
        {
            error = "DocumentId and OwnerId must be valid GUIDs.";
            return false;
        }

        var valid = parsed.Status switch
        {
            DocumentStatus.Extracting => parsed.FailureCode is null,
            DocumentStatus.Failed => DocumentFailureCodes.IsKnown(parsed.FailureCode),
            _ => false
        };
        if (!valid)
        {
            error = $"Status {parsed.Status} with failure code '{parsed.FailureCode}' is not accepted from the Extractor.";
            return false;
        }

        statusEvent = parsed;
        error = string.Empty;
        return true;
    }
}
