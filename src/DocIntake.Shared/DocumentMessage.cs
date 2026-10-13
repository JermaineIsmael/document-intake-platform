namespace DocIntake.Shared;

public sealed record DocumentMessage(
    string SourceUrl,
    Guid DocumentId,
    string Content);
