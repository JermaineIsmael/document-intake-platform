using System.Text.Json.Serialization;

namespace DocIntake.Shared;

[method: JsonConstructor]
public sealed record DocumentMessage(
    string SourceUrl,
    Guid DocumentId,
    string? Content,
    string OwnerId,
    DateTimeOffset CreatedAt,
    string SourceBlobName,
    string? ExtractedTextBlobName = null)
{
    public DocumentMessage(string sourceUrl, Guid documentId, string content)
        : this(sourceUrl, documentId, content, "00000000-0000-0000-0000-000000000001",
            DateTimeOffset.UnixEpoch, $"{documentId:D}/test.pdf")
    {
    }
}
