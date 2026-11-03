using Microsoft.AspNetCore.Cors.Infrastructure;

namespace DocIntake.Api.Configuration;

// Browser origins (for example the Static Web App front end) allowed to call the API cross-origin.
// No configured origins means no CORS policy: only same-origin and non-browser callers are served.
public static class CorsSettings
{
    public const string SectionName = "Cors";

    public static CorsPolicyBuilder Configure(CorsPolicyBuilder policy, string[] origins) => policy
        .WithOrigins(origins)
        .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Delete)
        .WithHeaders("Authorization", "Content-Type")
        .WithExposedHeaders("Location")
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10));

    public static string[] ReadAllowedOrigins(IConfiguration configuration)
    {
        var configured = configuration.GetSection($"{SectionName}:AllowedOrigins").Get<string[]>() ?? [];
        var origins = configured
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var invalid = origins.Where(origin => !IsValidOrigin(origin)).ToArray();
        if (invalid.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins must contain absolute HTTPS origins (HTTP is allowed only for localhost) "
                + $"without a path, query, or wildcard. Invalid: {string.Join(", ", invalid)}.");
        }

        return origins;
    }

    public static bool IsValidOrigin(string origin)
    {
        if (origin.Contains('*', StringComparison.Ordinal)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }
}
