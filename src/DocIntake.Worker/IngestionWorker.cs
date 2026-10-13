using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Search.Documents.Indexes;
using DocIntake.Shared;
using Microsoft.Extensions.Options;

namespace DocIntake.Worker;

public sealed class IngestionWorkerOptions
{
    public const string SectionName = "ServiceBus";

    public string QueueName { get; set; } = "extracted-documents";

    // Deliberate tuning knob: concurrent messages per worker instance. Higher values raise embedding
    // throughput but also Azure OpenAI TPM pressure (429s) and SQL/Search request concurrency.
    public int MaxConcurrentCalls { get; set; } = 4;

    // Must equal the queue's maxDeliveryCount (5 in infra/main.bicep) so the final attempt is
    // dead-lettered here with a descriptive reason rather than by Service Bus with a generic one.
    public int MaxDeliveryCount { get; set; } = 5;

    // The queue's lockDuration is PT1M. The processor renews the lock in the background while the
    // handler runs, up to this limit, so large documents are not redelivered mid-processing.
    public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(10);

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(QueueName)
        && MaxConcurrentCalls is >= 1 and <= 64
        && MaxDeliveryCount >= 1
        && MaxAutoLockRenewalDuration >= TimeSpan.FromMinutes(1)
        && MaxAutoLockRenewalDuration <= TimeSpan.FromHours(1);
}

public sealed class IngestionWorker(
    ServiceBusClient serviceBusClient,
    SearchIndexClient searchIndexClient,
    IChunkIndexer indexer,
    IOptions<IngestionWorkerOptions> options,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    internal const int MaxDeadLetterTextLength = 4096;
    private static readonly JsonSerializerOptions MessageJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IngestionWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Idempotent CreateOrUpdate; an exception here stops the host (BackgroundServiceExceptionBehavior.StopHost).
        await IndexSchema.EnsureAsync(searchIndexClient, stoppingToken).ConfigureAwait(false);
        logger.LogInformation("Search index {IndexName} is ready", IndexSchema.IndexName);

        // Lock duration vs. processing time: MaxAutoLockRenewalDuration keeps renewing the 1-minute
        // lock while IndexAsync runs (SDK-managed RenewMessageLockAsync). If renewal still fails
        // (network loss, a renewal limit, a crash), Service Bus redelivers the message; that is safe
        // because the SQL write is an upsert and the Search upload overwrites deterministic chunk ids.
        await using var processor = serviceBusClient.CreateProcessor(_options.QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            MaxConcurrentCalls = _options.MaxConcurrentCalls,
            MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration
        });
        processor.ProcessMessageAsync += HandleMessageAsync;
        processor.ProcessErrorAsync += HandleErrorAsync;

        await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
        logger.LogInformation(
            "Processing queue {QueueName} with MaxConcurrentCalls {MaxConcurrentCalls}",
            _options.QueueName,
            _options.MaxConcurrentCalls);

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
        var ct = args.CancellationToken;

        if (!TryParse(message, out var document, out var parseError))
        {
            // A malformed message fails identically on every delivery, so retrying only delays it.
            logger.LogError(
                "Message {MessageId} is not a valid DocumentMessage on delivery {DeliveryCount}: {Reason}",
                message.MessageId,
                message.DeliveryCount,
                parseError);
            await SettleAsync(() => args.DeadLetterMessageAsync(
                message, "InvalidDocumentMessage", Truncate(parseError), ct), message, "dead-letter").ConfigureAwait(false);
            return;
        }

        var documentId = document.DocumentId.ToString("D");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = documentId });

        try
        {
            await indexer.IndexAsync(document, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Processing of {DocumentId} was cancelled by shutdown; abandoning for redelivery", documentId);
            await SettleAsync(() => args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None), message, "abandon")
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            if (message.DeliveryCount >= _options.MaxDeliveryCount)
            {
                logger.LogError(
                    ex,
                    "Indexing failed for {DocumentId} on delivery {DeliveryCount} of {MaxDeliveryCount}; dead-lettering",
                    documentId,
                    message.DeliveryCount,
                    _options.MaxDeliveryCount);
                await SettleAsync(() => args.DeadLetterMessageAsync(
                    message,
                    "IndexingFailed",
                    Truncate($"{ex.GetType().FullName}: {ex.Message}"),
                    CancellationToken.None), message, "dead-letter").ConfigureAwait(false);
            }
            else
            {
                logger.LogError(
                    ex,
                    "Indexing failed for {DocumentId} on delivery {DeliveryCount} of {MaxDeliveryCount}; abandoning for redelivery",
                    documentId,
                    message.DeliveryCount,
                    _options.MaxDeliveryCount);
                await SettleAsync(() => args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None), message, "abandon")
                    .ConfigureAwait(false);
            }

            return;
        }

        await SettleAsync(() => args.CompleteMessageAsync(message, CancellationToken.None), message, "complete")
            .ConfigureAwait(false);
    }

    // Transport/infrastructure errors (connection, authorization, lock renewal), distinct from
    // message-handling failures, which HandleMessageAsync settles itself. The processor keeps
    // running and reconnecting, so these must be logged loudly rather than lost.
    internal Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(
            args.Exception,
            "Service Bus processor error from {ErrorSource} on {EntityPath} ({FullyQualifiedNamespace})",
            args.ErrorSource,
            args.EntityPath,
            args.FullyQualifiedNamespace);
        return Task.CompletedTask;
    }

    // If settlement fails (typically a lost lock), the message is redelivered after the lock expires.
    // That is safe because indexing is idempotent, so log and move on rather than fault the handler.
    private async Task SettleAsync(Func<Task> settle, ServiceBusReceivedMessage message, string action)
    {
        try
        {
            await settle().ConfigureAwait(false);
        }
        catch (ServiceBusException ex)
        {
            logger.LogWarning(
                ex,
                "Could not {Action} message {MessageId} (delivery {DeliveryCount}); it will be redelivered after its lock expires",
                action,
                message.MessageId,
                message.DeliveryCount);
        }
    }

    private static bool TryParse(ServiceBusReceivedMessage message, out DocumentMessage document, out string error)
    {
        document = null!;
        DocumentMessage? parsed;
        try
        {
            parsed = message.Body.ToObjectFromJson<DocumentMessage>(MessageJsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Body is not valid DocumentMessage JSON: {ex.Message}";
            return false;
        }

        if (parsed is null || parsed.DocumentId == Guid.Empty)
        {
            error = "DocumentId is missing.";
            return false;
        }

        if (!Uri.TryCreate(parsed.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
        {
            error = "SourceUrl must be an absolute HTTPS URL.";
            return false;
        }

        if (parsed.SourceUrl.Length > DocumentMetadataRepository.MaxSourceUrlLength)
        {
            error = $"SourceUrl exceeds {DocumentMetadataRepository.MaxSourceUrlLength} characters.";
            return false;
        }

        document = parsed with { Content = parsed.Content ?? string.Empty };
        error = string.Empty;
        return true;
    }

    private static string Truncate(string value) =>
        value.Length <= MaxDeadLetterTextLength ? value : value[..MaxDeadLetterTextLength];
}
