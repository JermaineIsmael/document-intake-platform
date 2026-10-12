using DocIntake.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocIntake.Api.Services;

public sealed record ValidatedFile(string SanitizedFileName, string ContentType);
public sealed record UploadError(string Error, int StatusCode);
public sealed record FileValidationResult(ValidatedFile? File, UploadError? Error);

public sealed class FileValidator(IOptions<UploadOptions> options)
{
    public async Task<FileValidationResult> ValidateAsync(
        IFormFile? file, CancellationToken cancellationToken = default)
    {
        if (file is null)
            return Failure("A file is required in the 'file' field.", StatusCodes.Status400BadRequest);
        if (file.Length == 0)
            return Failure("The file must not be empty.", StatusCodes.Status400BadRequest);
        if (file.Length > options.Value.MaxFileSizeBytes)
            return Failure("The file exceeds the configured size limit.", StatusCodes.Status413PayloadTooLarge);

        var extension = Path.GetExtension(file.FileName);
        if (!options.Value.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Failure("The file extension is not allowed.", StatusCodes.Status415UnsupportedMediaType);

        var isPdf = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        byte[] signature = isPdf ? "%PDF-"u8.ToArray() : [0x50, 0x4B, 0x03, 0x04];
        await using var stream = file.OpenReadStream();
        var header = new byte[signature.Length];
        var bytesRead = await stream.ReadAtLeastAsync(header, header.Length,
            throwOnEndOfStream: false, cancellationToken);
        if (bytesRead != signature.Length || !header.AsSpan().SequenceEqual(signature))
            return Failure("The file content does not match its extension.", StatusCodes.Status415UnsupportedMediaType);

        var sanitizedName = string.Concat(file.FileName.Where(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                or '.' or '_' or '-'));
        if (sanitizedName.Length > 255 || Uri.EscapeDataString(file.FileName).Length > 6000)
            return Failure("The filename is too long.", StatusCodes.Status400BadRequest);

        return new FileValidationResult(new ValidatedFile(sanitizedName,
            isPdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document"), null);
    }

    private static FileValidationResult Failure(string error, int statusCode) =>
        new(null, new UploadError(error, statusCode));
}
