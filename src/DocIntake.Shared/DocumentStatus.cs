using System.Text.Json.Serialization;

namespace DocIntake.Shared;

[JsonConverter(typeof(JsonStringEnumConverter<DocumentStatus>))]
public enum DocumentStatus
{
    Queued,
    Extracting,
    Processing,
    Indexed,
    NoText,
    Failed
}

// Stable, caller-safe failure codes. Never persist exception text as a failure code.
public static class DocumentFailureCodes
{
    public const string UnsupportedContent = "UnsupportedContent";
    public const string ExtractionFailed = "ExtractionFailed";
    public const string IndexingFailed = "IndexingFailed";
    public const string InvalidMessage = "InvalidMessage";
    public const string ProcessingTimedOut = "ProcessingTimedOut";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        UnsupportedContent, ExtractionFailed, IndexingFailed, InvalidMessage, ProcessingTimedOut
    };

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

public static class DocumentStatusTransitions
{
    public static bool IsTerminal(DocumentStatus status) =>
        status is DocumentStatus.Indexed or DocumentStatus.NoText or DocumentStatus.Failed;

    // Pipeline events can arrive out of order (status queue vs. document queue), so a status only
    // moves forward. A late success may replace a timeout/failure because the content was processed.
    public static bool CanTransition(DocumentStatus current, DocumentStatus next)
    {
        if (current == DocumentStatus.Failed)
        {
            return next is DocumentStatus.Indexed or DocumentStatus.NoText;
        }

        return Rank(next) > Rank(current);
    }

    public static int Rank(DocumentStatus status) => status switch
    {
        DocumentStatus.Queued => 0,
        DocumentStatus.Extracting => 1,
        DocumentStatus.Processing => 2,
        _ => 3
    };
}

// Published by the Extractor to the document-status queue and persisted by the Worker, because the
// Extractor has no private network path to SQL.
public sealed record DocumentStatusEvent(
    Guid DocumentId,
    string OwnerId,
    DocumentStatus Status,
    string? FailureCode,
    DateTimeOffset OccurredAt)
{
    // The status queue uses duplicate detection; one message per document and status.
    [JsonIgnore]
    public string MessageId =>  $"{DocumentId:D}:{Status}";
}
