using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DocIntake.Migrations;

public sealed record MigrationScript(string Name, string Checksum, IReadOnlyList<string> Batches);

public sealed record AppliedMigration(string Name, string Checksum);

public sealed class MigrationDriftException(string message) : Exception(message);

public static partial class MigrationScripts
{
    private const string ResourcePrefix = "DocIntake.Migrations.Scripts.";

    [GeneratedRegex(@"^\d{4}_[a-z0-9_]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptNamePattern();

    [GeneratedRegex(@"^[ \t]*GO[ \t]*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BatchSeparator();

    public static IReadOnlyList<MigrationScript> LoadEmbedded()
    {
        var assembly = typeof(MigrationScripts).Assembly;
        var scripts = new List<MigrationScript>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded migration {resource} could not be opened.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            scripts.Add(Create(resource[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return Order(scripts);
    }

    public static MigrationScript Create(string name, string content)
    {
        if (!ScriptNamePattern().IsMatch(name))
        {
            throw new InvalidOperationException(
                $"Migration '{name}' must be named NNNN_lower_snake_case.sql.");
        }

        var normalized = Normalize(content);
        var batches = SplitBatches(normalized);
        if (batches.Count == 0)
        {
            throw new InvalidOperationException($"Migration '{name}' contains no SQL.");
        }

        return new MigrationScript(name, ComputeChecksum(normalized), batches);
    }

    public static IReadOnlyList<MigrationScript> Order(IEnumerable<MigrationScript> scripts)
    {
        var ordered = scripts.OrderBy(script => script.Name, StringComparer.Ordinal).ToList();
        var duplicateVersion = ordered.GroupBy(script => script.Name[..4]).FirstOrDefault(group => group.Count() > 1);
        if (duplicateVersion is not null)
        {
            throw new InvalidOperationException($"Migration version {duplicateVersion.Key} is used more than once.");
        }

        return ordered;
    }

    // Line endings and trailing whitespace are normalized so a Git autocrlf checkout does not look like drift.
    public static string Normalize(string content) =>
        string.Join('\n', content.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.TrimEnd()))
            .Trim();

    public static string ComputeChecksum(string normalizedContent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedContent)));

    public static IReadOnlyList<string> SplitBatches(string content) =>
        BatchSeparator().Split(content)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0 && !IsCommentOnly(batch))
            .ToList();

    private static bool IsCommentOnly(string batch) =>
        batch.Split('\n').All(line => line.Trim().Length == 0 || line.TrimStart().StartsWith("--", StringComparison.Ordinal));
}

public static class MigrationPlanner
{
    // Applied scripts must match the shipped content exactly, and the database must not contain
    // scripts this build does not know about (that would mean running older code against a newer schema).
    public static IReadOnlyList<MigrationScript> GetPending(
        IReadOnlyList<MigrationScript> scripts, IReadOnlyCollection<AppliedMigration> applied)
    {
        var known = scripts.ToDictionary(script => script.Name, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var migration in applied)
        {
            if (!known.TryGetValue(migration.Name, out var script))
            {
                problems.Add($"'{migration.Name}' is applied in the database but not shipped in this build");
            }
            else if (!string.Equals(script.Checksum, migration.Checksum.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"'{migration.Name}' was modified after it was applied (checksum mismatch)");
            }
        }

        if (problems.Count > 0)
        {
            throw new MigrationDriftException("Schema drift detected: " + string.Join("; ", problems) + ".");
        }

        var appliedNames = applied.Select(migration => migration.Name).ToHashSet(StringComparer.Ordinal);
        var pending = scripts.Where(script => !appliedNames.Contains(script.Name)).ToList();
        var lastApplied = applied.Select(migration => migration.Name).DefaultIfEmpty(string.Empty)
            .Max(StringComparer.Ordinal) ?? string.Empty;
        var outOfOrder = pending.FirstOrDefault(script => string.CompareOrdinal(script.Name, lastApplied) < 0);
        if (outOfOrder is not null)
        {
            throw new MigrationDriftException(
                $"Migration '{outOfOrder.Name}' is older than the latest applied migration '{lastApplied}'; renumber it.");
        }

        return pending;
    }
}
