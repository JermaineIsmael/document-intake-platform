using System.Text;

namespace DocIntake.Api.Services;

public static class RagContextBuilder
{
    public const int MaxContextCharacters = 12_000;
    public const string ContextEndTag = "</document_context>";

    // Returns null when no usable excerpt remains. Excerpts are grouped per document in retrieval order,
    // so the nth label ([source1], [source2], ...) corresponds to the nth returned source.
    public static RagContext? Build(IReadOnlyList<RetrievedChunk> chunks, int maxCharacters = MaxContextCharacters)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<(string DocumentId, string Source, List<string> Excerpts)>();
        var remaining = maxCharacters;

        foreach (var chunk in chunks)
        {
            if (remaining <= 0)
            {
                break;
            }

            var content = Sanitize(chunk.Content);
            if (content.Length == 0 || !seenContent.Add(content))
            {
                continue;
            }

            if (content.Length > remaining)
            {
                content = content[..remaining];
            }

            remaining -= content.Length;
            var index = documents.FindIndex(d => d.DocumentId == chunk.DocumentId);
            if (index < 0)
            {
                documents.Add((chunk.DocumentId, chunk.Source, [content]));
            }
            else
            {
                documents[index].Excerpts.Add(content);
            }
        }

        if (documents.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        for (var i = 0; i < documents.Count; i++)
        {
            text.Append($"[source{i + 1}]\n");
            foreach (var excerpt in documents[i].Excerpts)
            {
                text.Append(excerpt).Append("\n\n");
            }
        }

        var sources = documents.Select(d => d.Source).ToArray();
        return new RagContext(text.ToString().TrimEnd(), sources);
    }

    // Document text must not be able to close the delimiter that frames it as untrusted data.
    private static string Sanitize(string? content) =>
        (content ?? string.Empty).Replace(ContextEndTag, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
}
