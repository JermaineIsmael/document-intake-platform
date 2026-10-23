using DocIntake.Shared;

namespace DocIntake.Api.Services;

public static class DocumentStatusPresenter
{
    public const string Deleted = "deleted";

    public static string ToApiValue(DocumentStatus status) => status switch
    {
        DocumentStatus.Queued => "queued",
        DocumentStatus.Extracting => "extracting",
        DocumentStatus.Processing => "processing",
        DocumentStatus.Indexed => "indexed",
        DocumentStatus.NoText => "noText",
        DocumentStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown document status.")
    };

    public static DocumentFailure DescribeFailure(string? code) => code switch
    {
        DocumentFailureCodes.UnsupportedContent => new(code,
            "The document could not be read. Upload a valid, unencrypted PDF or DOCX file.", false),
        DocumentFailureCodes.ExtractionFailed => new(code,
            "Text extraction failed. Upload the document again.", true),
        DocumentFailureCodes.IndexingFailed => new(code,
            "The document could not be indexed. Upload the document again.", true),
        DocumentFailureCodes.InvalidMessage => new(code,
            "The document could not be processed. Upload the document again.", true),
        DocumentFailureCodes.ProcessingTimedOut => new(code,
            "Processing did not complete in time. Upload the document again.", true),
        _ => new("ProcessingFailed", "The document could not be processed.", true)
    };

    public static DocumentStatusResponse ToResponse(Guid documentId, DocumentMetadata document)
    {
        var processingStatus = ToApiValue(document.Status);
        return new DocumentStatusResponse(
            documentId,
            document.DeletedAt is not null ? Deleted : processingStatus,
            processingStatus,
            document.StatusUpdatedAt,
            document.ChunkCount,
            document.CreatedAt,
            document.IndexedAt,
            document.DeletedAt,
            document.RecoverableUntil,
            document.Status == DocumentStatus.Failed ? DescribeFailure(document.FailureCode) : null);
    }
}
