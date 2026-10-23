using Azure.Storage.Blobs;

namespace DocIntake.Worker;

public sealed class BlobExtractedTextReader(BlobContainerClient container) : IExtractedTextReader
{
    public async Task<string> ReadAsync(string blobName, Guid documentId, string ownerId, CancellationToken ct)
    {
        var expectedPrefix = $"{documentId:D}/";
        if (!blobName.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || blobName.Contains("..", StringComparison.Ordinal)
            || blobName.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Extracted-text reference is outside its document namespace.");
        }

        var blob = container.GetBlobClient(blobName);
        var properties = await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        if (!properties.Value.Metadata.TryGetValue("documentId", out var storedDocumentId)
            || !Guid.TryParse(storedDocumentId, out var parsedDocumentId)
            || parsedDocumentId != documentId
            || !properties.Value.Metadata.TryGetValue("ownerId", out var storedOwnerId)
            || !string.Equals(storedOwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Extracted-text blob ownership metadata does not match the queue message.");
        }

        var response = await blob.DownloadContentAsync(ct).ConfigureAwait(false);
        return response.Value.Content.ToString();
    }
}
