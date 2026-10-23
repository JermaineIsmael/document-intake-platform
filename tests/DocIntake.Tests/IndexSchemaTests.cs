using Azure.Search.Documents.Indexes.Models;
using DocIntake.Worker;
using FluentAssertions;

namespace DocIntake.Tests;

public sealed class IndexSchemaTests
{
    [Fact]
    public void Build_DefinesDocumentChunksIndexWithExpectedFields()
    {
        var index = IndexSchema.Build();

        index.Name.Should().Be("document-chunks");
        index.Fields.Select(f => f.Name).Should().Equal(
            "id", "documentId", "ownerId", "isDeleted", "content", "source", "contentVector");

        var id = index.Fields.Single(f => f.Name == "id");
        id.IsKey.Should().BeTrue();
        id.Type.Should().Be(SearchFieldDataType.String);
        index.Fields.Single(f => f.Name == "documentId").IsFilterable.Should().BeTrue();
        index.Fields.Single(f => f.Name == "ownerId").IsFilterable.Should().BeTrue();
        index.Fields.Single(f => f.Name == "isDeleted").IsFilterable.Should().BeTrue();

        index.Fields.Single(f => f.Name == "content").IsSearchable.Should().BeTrue();
        index.Fields.Single(f => f.Name == "source").IsFilterable.Should().BeTrue();

        var vector = index.Fields.Single(f => f.Name == "contentVector");
        vector.Type.Should().Be(SearchFieldDataType.Collection(SearchFieldDataType.Single));
        vector.IsSearchable.Should().BeTrue();
        vector.VectorSearchDimensions.Should().Be(1536);
        vector.VectorSearchProfileName.Should().Be(IndexSchema.VectorProfileName);
    }

    [Fact]
    public void Build_VectorProfileReferencesHnswAlgorithm()
    {
        var vectorSearch = IndexSchema.Build().VectorSearch;

        vectorSearch.Algorithms.Should().ContainSingle().Which.Should().BeOfType<HnswAlgorithmConfiguration>()
            .Which.Name.Should().Be(IndexSchema.HnswConfigurationName);
        vectorSearch.Profiles.Should().ContainSingle().Which.AlgorithmConfigurationName
            .Should().Be(IndexSchema.HnswConfigurationName);
    }
}
