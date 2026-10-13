namespace DocIntake.Shared;

public static class TextChunker
{
    public static IReadOnlyList<string> Split(string? text, int size = 800, int overlap = 100)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Chunk size must be positive.");
        }

        if (overlap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), overlap, "Overlap cannot be negative.");
        }

        if (overlap >= size)
        {
            throw new ArgumentException("Overlap must be smaller than chunk size, otherwise the window never advances.", nameof(overlap));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var step = size - overlap;
        var chunks = new List<string>();

        for (var start = 0; ; start += step)
        {
            var length = Math.Min(size, text.Length - start);
            chunks.Add(text.Substring(start, length));

            if (start + size >= text.Length)
            {
                break;
            }
        }

        return chunks;
    }
}
