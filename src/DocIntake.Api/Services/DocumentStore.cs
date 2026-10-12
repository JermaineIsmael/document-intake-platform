using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DocIntake.Api.Services;

public sealed class DocumentStore(BlobContainerClient container)
{
    public static BlobContainerClient CreateContainerClient(
        IConfiguration configuration, IHostEnvironment environment)
    {
        var endpoint = configuration["Storage:BlobEndpoint"];
        var containerName = configuration["Storage:ContainerName"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(containerName))
            throw new InvalidOperationException("Storage:BlobEndpoint and Storage:ContainerName must be configured.");

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
        CancellationToken cancellationToken = default)
    {
        if (container.Uri.AbsoluteUri.StartsWith(
            "http://127.0.0.1:10000/devstoreaccount1/", StringComparison.Ordinal))
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var blob = container.GetBlobClient($"{documentId:D}/{validatedFile.SanitizedFileName}");
        await using var stream = file.OpenReadStream();
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = validatedFile.ContentType },
            Metadata = new Dictionary<string, string>
            {
                // Blob metadata headers require ASCII; escaping preserves the original filename.
                ["originalName"] = Uri.EscapeDataString(file.FileName),
                ["documentId"] = documentId.ToString("D")
            },
            Conditions = new BlobRequestConditions
            {
                IfNoneMatch = Azure.ETag.All
            }
        }, cancellationToken);
    }
}
