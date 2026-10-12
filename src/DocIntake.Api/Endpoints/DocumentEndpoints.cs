using Azure;
using DocIntake.Api.Configuration;
using DocIntake.Api.Services;
using DocIntake.Shared;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DocIntake.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this WebApplication app) =>
        app.MapPost("/documents", UploadAsync).RequireAuthorization();

    private static async Task<IResult> UploadAsync(HttpRequest request, FileValidator validator,
        DocumentStore store, IOptions<UploadOptions> options,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("DocIntake.Api.Endpoints.DocumentEndpoints");
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
        try
        {
            await store.UploadAsync(documentId, file, result.File, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            logger.LogError(exception, "Document blob upload failed for {DocumentId}", documentId);
            return Results.Json(new { error = "The document could not be stored. Please retry." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Document upload accepted: {DocumentId}, {FileSizeBytes} bytes", documentId, file.Length);
        return Results.Accepted($"/documents/{documentId:D}", new DocumentAccepted(documentId, "queued"));
    }
}
