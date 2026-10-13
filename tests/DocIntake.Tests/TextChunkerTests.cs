using DocIntake.Shared;
using FluentAssertions;

namespace DocIntake.Tests;

public sealed class TextChunkerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Split_NullEmptyOrWhitespace_ReturnsNoChunks(string? text)
    {
        TextChunker.Split(text).Should().BeEmpty();
    }

    [Fact]
    public void Split_TextShorterThanSize_ReturnsSingleChunkEqualToText()
    {
        var text = new string('a', 799);

        TextChunker.Split(text).Should().ContainSingle().Which.Should().Be(text);
    }

    [Fact]
    public void Split_TextExactlySize_ReturnsSingleChunk()
    {
        var text = new string('a', 800);

        TextChunker.Split(text).Should().ContainSingle().Which.Should().Be(text);
    }

    [Theory]
    [InlineData(2500, 800, 100)]
    [InlineData(2200, 800, 100)]
    [InlineData(1000, 300, 50)]
    [InlineData(101, 10, 9)]
    public void Split_LongText_ProducesWindowsThatOverlapByConfiguredAmount(int length, int size, int overlap)
    {
        // Non-repeating within any window, so a chunk taken at the wrong offset cannot compare equal.
        var text = string.Concat(Enumerable.Range(0, length).Select(i => (char)('!' + i % 90)));
        var step = size - overlap;
        var expectedCount = length <= size ? 1 : (int)Math.Ceiling((double)(length - size) / step) + 1;

        var chunks = TextChunker.Split(text, size, overlap);

        chunks.Should().HaveCount(expectedCount);
        for (var i = 0; i < chunks.Count; i++)
        {
            var start = i * step;
            chunks[i].Should().Be(text.Substring(start, Math.Min(size, length - start)));
        }

        for (var i = 1; i < chunks.Count; i++)
        {
            chunks[i - 1][^overlap..].Should().Be(chunks[i][..overlap]);
        }

        chunks.Take(chunks.Count - 1).Should().OnlyContain(c => c.Length == size);
        chunks[^1].Length.Should().BeInRange(overlap + 1, size);
        chunks[^1].Should().Be(text[((chunks.Count - 1) * step)..]);
    }

    [Fact]
    public void Split_DefaultArguments_UseSize800AndOverlap100()
    {
        var text = new string('x', 1500);

        var chunks = TextChunker.Split(text);

        chunks.Select(c => c.Length).Should().Equal(800, 800);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(100, 101)]
    public void Split_OverlapGreaterThanOrEqualToSize_Throws(int size, int overlap)
    {
        var act = () => TextChunker.Split("some text", size, overlap);

        act.Should().Throw<ArgumentException>().WithParameterName("overlap");
    }
}
