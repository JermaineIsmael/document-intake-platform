using DocIntake.Api.Services;
using DocIntake.Shared;
using FluentAssertions;

namespace DocIntake.Tests;

public sealed class DocumentStatusTests
{
    [Theory]
    [InlineData(DocumentStatus.Queued, DocumentStatus.Extracting, true)]
    [InlineData(DocumentStatus.Queued, DocumentStatus.Processing, true)]
    [InlineData(DocumentStatus.Extracting, DocumentStatus.Indexed, true)]
    [InlineData(DocumentStatus.Processing, DocumentStatus.Extracting, false)]
    [InlineData(DocumentStatus.Indexed, DocumentStatus.Processing, false)]
    [InlineData(DocumentStatus.Indexed, DocumentStatus.Failed, false)]
    [InlineData(DocumentStatus.NoText, DocumentStatus.Indexed, false)]
    [InlineData(DocumentStatus.Failed, DocumentStatus.Indexed, true)]
    [InlineData(DocumentStatus.Failed, DocumentStatus.NoText, true)]
    [InlineData(DocumentStatus.Failed, DocumentStatus.Processing, false)]
    [InlineData(DocumentStatus.Processing, DocumentStatus.Processing, false)]
    public void Statuses_only_move_forward(DocumentStatus current, DocumentStatus next, bool allowed) =>
        DocumentStatusTransitions.CanTransition(current, next).Should().Be(allowed);

    [Fact]
    public void Status_serializes_as_its_name()
    {
        var json = BinaryData.FromObjectAsJson(
            new DocumentStatusEvent(Guid.Empty, "owner", DocumentStatus.NoText, null, DateTimeOffset.UnixEpoch),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).ToString();

        json.Should().Contain("\"status\":\"NoText\"").And.NotContain("messageId");
    }

    [Fact]
    public void Every_status_has_an_api_value()
    {
        Enum.GetValues<DocumentStatus>().Select(DocumentStatusPresenter.ToApiValue)
            .Should().Equal("queued", "extracting", "processing", "indexed", "noText", "failed");
    }

    [Fact]
    public void Every_failure_code_has_a_specific_message_and_unknown_codes_are_generic()
    {
        foreach (var code in DocumentFailureCodes.All)
        {
            DocumentStatusPresenter.DescribeFailure(code).Code.Should().Be(code);
        }

        DocumentStatusPresenter.DescribeFailure("Internal stack trace").Should()
            .Be(new DocumentFailure("ProcessingFailed", "The document could not be processed.", true));
    }

    [Fact]
    public void Deleted_documents_report_deleted_but_keep_the_processing_status()
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new DocumentMetadata("id", "url", "blob", "owner", 2, now, now, now, now.AddDays(30), null,
            DocumentStatus.Indexed, now);

        var response = DocumentStatusPresenter.ToResponse(Guid.NewGuid(), metadata);

        response.Status.Should().Be("deleted");
        response.ProcessingStatus.Should().Be("indexed");
        response.RecoverableUntil.Should().Be(now.AddDays(30));
    }
}
