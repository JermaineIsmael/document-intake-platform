using Azure;
using DocIntake.Api.Configuration;
using DocIntake.Api.Services;
using DocIntake.Shared;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DocIntake.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this WebApplication app)
    {
        app.MapPost("/documents", UploadAsync).RequireAuthorization();
        app.MapGet("/documents/{documentId:guid}", GetAsync).RequireAuthorization();
        app.MapDelete("/documents/{documentId:guid}", DeleteAsync).RequireAuthorization();
        app.MapPost("/documents/{documentId:guid}/restore", RestoreAsync).RequireAuthorization();
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, FileValidator validator,
        DocumentStore store, DocumentLifecycleService lifecycle, IOptions<UploadOptions> options,
        IHostEnvironment environment, IConfiguration configuration,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("DocIntake.Api.Endpoints.DocumentEndpoints");
        if (!DocumentOwner.TryGet(request.HttpContext.User, environment, configuration, out var ownerId))
        {
            return Results.Forbid();
        }

        IResult Reject(string reason, int statusCode)
        {
            logger.LogWarning("Document upload validation failed: {Reason}", reason);
            return Results.Json(new { error = reason }, statusCode: statusCode);
        }

        if (request.ContentLength > options.Value.MaxRequestBodySize)
            return Reject("The request exceeds the configured size limit.", StatusCodes.Status413PayloadTooLarge);
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Reject("A multipart/form-data request with a 'file' field is required.", StatusCodes.Status400BadRequest);

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException exception)
        {
            return Reject(exception.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? "The request exceeds the configured size limit."
                : "The multipart request is invalid.", exception.StatusCode);
        }
        catch (InvalidDataException exception)
        {
            // .NET's multipart parser reports its configured per-section limit via this exception.
            var exceedsLimit = exception.Message.StartsWith("Multipart body length limit ", StringComparison.Ordinal)
                && exception.Message.EndsWith(" exceeded.", StringComparison.Ordinal);
            return Reject(exceedsLimit ? "The file exceeds the configured size limit." : "The multipart request is invalid.",
                exceedsLimit ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest);
        }

        var file = form.Files.GetFile("file");
        if (form.Files.Count > 1)
            return Reject("Exactly one file must be provided.", StatusCodes.Status400BadRequest);

        var result = await validator.ValidateAsync(file, cancellationToken);
        if (result.Error is { } error)
            return Reject(error.Error, error.StatusCode);
        if (file is null || result.File is null)
            throw new InvalidOperationException("Successful validation must include a file.");

        var documentId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var blobName = store.GetBlobName(documentId, result.File);
        try
        {
            await lifecycle.RegisterAsync(new DocumentMetadata(
                documentId.ToString("D"), store.GetBlobUri(blobName),
                blobName, ownerId, 0, createdAt, null, null, null, null), cancellationToken);
        }
        catch (Exception exception) when (exception is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            logger.LogError(exception, "Document metadata registration failed for {DocumentId}", documentId);
            return Results.Json(new { error = "The document could not be registered. Please retry." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            await store.UploadAsync(documentId, file, result.File, ownerId, createdAt, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            logger.LogError(exception, "Document blob upload failed for {DocumentId}", documentId);
            try
            {
                await store.DeleteAsync(blobName, documentId, CancellationToken.None);
                await lifecycle.CancelPendingUploadAsync(documentId.ToString("D"), ownerId, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogError(cleanupException, "Could not clean up failed upload {DocumentId}", documentId);
            }

            return Results.Json(new { error = "The document could not be stored. Please retry." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Document upload accepted: {DocumentId}, {FileSizeBytes} bytes", documentId, file.Length);
        return Results.Accepted($"/documents/{documentId:D}", new DocumentAccepted(documentId, "queued"));
    }

    private static async Task<IResult> GetAsync(
        Guid documentId,
        HttpContext context,
        DocumentLifecycleService lifecycle,
        IHostEnvironment environment,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (!DocumentOwner.TryGet(context.User, environment, configuration, out var ownerId))
        {
            return Results.Forbid();
        }

        DocumentMetadata? document;
        try
        {
            document = await lifecycle.GetOwnedAsync(documentId.ToString("D"), ownerId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (document is null)
        {
            return Results.NotFound();
        }

        var status = document.DeletedAt is not null ? "deleted"
            : document.ChunkCount > 0 ? "indexed"
            : "queued";
        return Results.Ok(new
        {
            documentId,
            status,
            document.ChunkCount,
            document.CreatedAt,
            document.IndexedAt,
            document.DeletedAt,
            document.RecoverableUntil
        });
    }

    private static async Task<IResult> DeleteAsync(
        Guid documentId,
        HttpContext context,
        DocumentLifecycleService lifecycle,
        IHostEnvironment environment,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!DocumentOwner.TryGet(context.User, environment, configuration, out var ownerId))
        {
            return Results.Forbid();
        }

        try
        {
            return await lifecycle.DeleteAsync(documentId.ToString("D"), ownerId, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (Exception exception) when (exception is RequestFailedException or Microsoft.Data.SqlClient.SqlException)
        {
            loggerFactory.CreateLogger("DocIntake.Api.Endpoints.DocumentEndpoints")
                .LogError(exception, "Failed to delete document {DocumentId}", documentId);
            return Results.Json(new { error = "Document deletion is incomplete and can be retried." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> RestoreAsync(
        Guid documentId,
        HttpContext context,
        DocumentLifecycleService lifecycle,
        IHostEnvironment environment,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!DocumentOwner.TryGet(context.User, environment, configuration, out var ownerId))
        {
            return Results.Forbid();
        }

        try
        {
            return await lifecycle.RestoreAsync(documentId.ToString("D"), ownerId, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (Exception exception) when (exception is RequestFailedException
            or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            loggerFactory.CreateLogger("DocIntake.Api.Endpoints.DocumentEndpoints")
                .LogError(exception, "Failed to restore document {DocumentId}", documentId);
            return Results.Json(new { error = "Document restoration failed; retry within the recovery window." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
