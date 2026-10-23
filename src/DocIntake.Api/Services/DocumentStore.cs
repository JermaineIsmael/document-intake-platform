using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DocIntake.Api.Services;

public sealed class DocumentStore(BlobContainerClient container, BlobContainerClient extractedTextContainer)
{
    public string GetBlobName(Guid documentId, ValidatedFile file) =>
        $"{documentId:D}/{file.SanitizedFileName}";

    public string GetExtractedTextBlobName(Guid documentId) => $"{documentId:D}/content.txt";

    public string GetBlobUri(string blobName) => container.GetBlobClient(blobName).Uri.AbsoluteUri;

    public static BlobContainerClient CreateContainerClient(
        IConfiguration configuration, IHostEnvironment environment)
        => CreateContainerClient(configuration, environment,
            configuration["Storage:ContainerName"], "Storage:ContainerName");

    public static BlobContainerClient CreateExtractedTextContainerClient(
        IConfiguration configuration, IHostEnvironment environment)
        => CreateContainerClient(configuration, environment,
            configuration["Storage:ExtractedTextContainer"] ?? "extracted-text",
            "Storage:ExtractedTextContainer");

    private static BlobContainerClient CreateContainerClient(
        IConfiguration configuration, IHostEnvironment environment, string? containerName, string containerKey)
    {
        var endpoint = configuration["Storage:BlobEndpoint"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(containerName))
            throw new InvalidOperationException($"Storage:BlobEndpoint and {containerKey} must be configured.");

        if (environment.IsDevelopment()
            && uri.AbsoluteUri.TrimEnd('/') == "http://127.0.0.1:10000/devstoreaccount1")
        {
            // Azurite's public emulator credentials are never used for Azure endpoints.
            return new BlobServiceClient("UseDevelopmentStorage=true",
                new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03))
                .GetBlobContainerClient(containerName);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Azure Storage:BlobEndpoint must use HTTPS.");

        return new BlobServiceClient(uri, new DefaultAzureCredential()).GetBlobContainerClient(containerName);
    }

    public async Task UploadAsync(Guid documentId, IFormFile file, ValidatedFile validatedFile,
        string ownerId, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        if (container.Uri.AbsoluteUri.StartsWith(
            "http://127.0.0.1:10000/devstoreaccount1/", StringComparison.Ordinal))
        {
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            await extractedTextContainer.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        }

        var blob = container.GetBlobClient(GetBlobName(documentId, validatedFile));
        await using var stream = file.OpenReadStream();
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = validatedFile.ContentType },
            Metadata = new Dictionary<string, string>
            {
                // Blob metadata headers require ASCII; escaping preserves the original filename.
                ["originalName"] = Uri.EscapeDataString(file.FileName),
                ["documentId"] = documentId.ToString("D"),
                ["ownerId"] = ownerId,
                ["createdAt"] = createdAt.ToString("O")
            },
            Conditions = new BlobRequestConditions
            {
                IfNoneMatch = Azure.ETag.All
            }
        }, cancellationToken);
    }

    public async Task DeleteAsync(string blobName, Guid documentId, CancellationToken ct)
    {
        ValidateDocumentBlobName(blobName, documentId);
        await container.GetBlobClient(blobName)
            .DeleteIfExistsAsync(Azure.Storage.Blobs.Models.DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: ct).ConfigureAwait(false);
        await extractedTextContainer.GetBlobClient(GetExtractedTextBlobName(documentId))
            .DeleteIfExistsAsync(Azure.Storage.Blobs.Models.DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task RestoreAsync(string blobName, Guid documentId, CancellationToken ct)
    {
        ValidateDocumentBlobName(blobName, documentId);
        await RestoreIfDeletedAsync(container.GetBlobClient(blobName), ct).ConfigureAwait(false);
        await RestoreIfDeletedAsync(extractedTextContainer.GetBlobClient(GetExtractedTextBlobName(documentId)), ct)
            .ConfigureAwait(false);
    }

    private static void ValidateDocumentBlobName(string blobName, Guid documentId)
    {
        if (!blobName.StartsWith($"{documentId:D}/", StringComparison.Ordinal)
            || blobName.Contains("..", StringComparison.Ordinal)
            || blobName.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Document blob reference is outside its document namespace.");
        }
    }

    private static async Task RestoreIfDeletedAsync(Azure.Storage.Blobs.BlobClient blob, CancellationToken ct)
    {
        try
        {
            await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == 404)
        {
            await blob.UndeleteAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    }
}
