namespace DocIntake.Shared;

// Response of GET /documents/{documentId}. Status values are camelCase: queued, extracting,
// processing, indexed, noText, failed, or deleted (owner deletion/retention takes precedence).
public sealed record DocumentStatusResponse(
    Guid DocumentId,
    string Status,
    string ProcessingStatus,
    DateTimeOffset? StatusUpdatedAt,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IndexedAt,
    DateTimeOffset? DeletedAt,
    DateTimeOffset? RecoverableUntil,
    DocumentFailure? Failure);

// Caller-safe failure details: a stable code, a user-facing message, and whether uploading the
// document again may succeed. Internal exception details are never returned.
public sealed record DocumentFailure(string Code, string Message, bool Retryable);
