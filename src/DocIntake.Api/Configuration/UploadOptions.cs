namespace DocIntake.Api.Configuration;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";
    public int MaxFileSizeMb { get; set; } = 25;
    public string[] AllowedExtensions { get; set; } = [".pdf", ".docx"];
    public long MaxFileSizeBytes => MaxFileSizeMb * 1024L * 1024L;

    // Multipart boundaries and headers must not reduce the permitted file size.
    public long MaxRequestBodySize => MaxFileSizeBytes + 64 * 1024;

    public bool IsValid() => MaxFileSizeMb > 0
        && AllowedExtensions is { Length: > 0 }
        && AllowedExtensions.All(extension =>
            string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase));
}
