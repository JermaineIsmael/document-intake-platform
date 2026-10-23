using System.Security.Claims;

namespace DocIntake.Api.Endpoints;

internal static class DocumentOwner
{
    private const string DevelopmentOwnerId = "00000000-0000-0000-0000-000000000001";

    public static bool TryGet(
        ClaimsPrincipal user, IHostEnvironment environment, IConfiguration configuration, out string ownerId)
    {
        var claim = user.FindFirst("oid")?.Value;
        if (Guid.TryParse(claim, out var objectId))
        {
            ownerId = objectId.ToString("D");
            return true;
        }

        if (environment.IsDevelopment() && configuration.GetValue<bool>("Development:BypassAuth"))
        {
            ownerId = DevelopmentOwnerId;
            return true;
        }

        ownerId = string.Empty;
        return false;
    }
}
