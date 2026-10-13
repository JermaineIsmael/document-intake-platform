using System.Text.Json;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DocIntake.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DocIntake.Extractor;

public sealed class ExtractDocument(
    BlobServiceClient blobServiceClient,
    DocumentIntelligenceClient documentIntelligenceClient,
    ServiceBusSender sender,
    IConfiguration configuration,
    ILogger<ExtractDocument> logger)
{
    [Function(nameof(ExtractDocument))]
    public async Task Run(
        [EventGridTrigger] EventGridEvent eventGridEvent,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(eventGridEvent.EventType, "Microsoft.Storage.BlobCreated",
            StringComparison.Ordinal))
        {
            logger.LogInformation("Ignoring unsupported event type {EventType}", eventGridEvent.EventType);
            return;
        }

        using var eventData = JsonDocument.Parse(eventGridEvent.Data.ToStream());
        if (!eventData.RootElement.TryGetProperty("url", out var urlElement)
            || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var blobUri))
        {
            throw new InvalidOperationException("Storage BlobCreated event did not contain a valid blob URL.");
        }

        var endpoint = configuration["Storage:BlobEndpoint"];
        var containerName = configuration["Storage:ContainerName"] ?? "documents";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var storageEndpoint)
            || !string.Equals(blobUri.Host, storageEndpoint.Host, StringComparison.OrdinalIgnoreCase)
            || blobUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The event blob URL does not match the configured HTTPS storage account.");
        }

        var pathSegments = Uri.UnescapeDataString(blobUri.AbsolutePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var expectedPrefixSegments = storageEndpoint.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Append(containerName)
            .ToArray();
        if (pathSegments.Length != expectedPrefixSegments.Length + 2
            || !pathSegments.Take(expectedPrefixSegments.Length)
                .SequenceEqual(expectedPrefixSegments, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The event blob URL is outside the configured documents container.");
        }

        var documentIdSegment = pathSegments[^2];
        if (!Guid.TryParse(documentIdSegment, out var documentId))
        {
            throw new InvalidOperationException("Storage BlobCreated event URL has no valid document ID segment.");
        }

        try
        {
            var fileName = pathSegments[^1];
            var blob = blobServiceClient.GetBlobContainerClient(containerName)
                .GetBlobClient($"{documentIdSegment}/{fileName}");
            var downloaded = await blob.DownloadContentAsync(cancellationToken);
            var operation = await documentIntelligenceClient.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                "prebuilt-read",
                downloaded.Value.Content,
                cancellationToken: cancellationToken);

            var message = new DocumentMessage(
                blobUri.AbsoluteUri,
                documentId,
                operation.Value.Content ?? string.Empty);
            var serviceBusMessage = new ServiceBusMessage(
                BinaryData.FromObjectAsJson(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            {
                MessageId = documentId.ToString("D"),
                ContentType = "application/json"
            };

            using var messageBatch = await sender.CreateMessageBatchAsync(cancellationToken);
            if (!messageBatch.TryAddMessage(serviceBusMessage))
            {
                throw new InvalidOperationException(
                    "The extracted document does not fit in a Service Bus message.");
            }

            await sender.SendMessagesAsync(messageBatch, cancellationToken);
        }
        // Rethrow so Event Grid retries delivery; exhausted retries land in Event Grid's dead-letter destination if one is configured (see Task 7).
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to extract and queue document {DocumentId} from blob {BlobUrl}",
                documentId,
                blobUri.AbsoluteUri);
            throw;
        }

        logger.LogInformation("Extracted {documentId} and queued for indexing", documentId);
    }
}
