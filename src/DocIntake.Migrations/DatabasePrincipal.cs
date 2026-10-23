using System.Text.RegularExpressions;

namespace DocIntake.Migrations;

// A managed identity that receives a contained database user. The user is created from the identity's
// client (application) ID as its SID, so no Microsoft Graph lookup or Directory Readers role is required.
public sealed partial record DatabasePrincipal(string Name, Guid ClientId, string Role)
{
    public static readonly IReadOnlySet<string> AllowedRoles =
        new HashSet<string>(StringComparer.Ordinal) { "docintake_api", "docintake_worker" };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public byte[] Sid => ClientId.ToByteArray();

    public static DatabasePrincipal Create(string? name, string? clientId, string role)
    {
        if (string.IsNullOrWhiteSpace(name) || !NamePattern().IsMatch(name))
        {
            throw new InvalidOperationException(
                $"Database principal name for role {role} is missing or contains unsupported characters.");
        }

        if (!Guid.TryParse(clientId, out var parsedClientId) || parsedClientId == Guid.Empty)
        {
            throw new InvalidOperationException($"Client ID for database principal '{name}' must be a GUID.");
        }

        if (!AllowedRoles.Contains(role))
        {
            throw new InvalidOperationException($"Role '{role}' is not an application role.");
        }

        return new DatabasePrincipal(name, parsedClientId, role);
    }
}
