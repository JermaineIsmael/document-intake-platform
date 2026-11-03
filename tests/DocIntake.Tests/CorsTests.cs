using System.Net;
using DocIntake.Api.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DocIntake.Tests;

public sealed class CorsTests
{
    private const string AllowedOrigin = "https://docintake-web.example.net";

    [Fact]
    public async Task Preflight_from_allowed_origin_succeeds_without_a_token()
    {
        await using var factory = CreateFactory(AllowedOrigin);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/documents", AllowedOrigin, "POST"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be(AllowedOrigin);
        response.Headers.GetValues("Access-Control-Allow-Headers").Single().Should()
            .ContainEquivalentOf("authorization");
    }

    [Fact]
    public async Task Preflight_from_unlisted_origin_is_not_allowed()
    {
        await using var factory = CreateFactory(AllowedOrigin);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/documents", "https://evil.example", "POST"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Unauthenticated_cross_origin_request_is_still_rejected_but_readable_by_the_browser()
    {
        await using var factory = CreateFactory(AllowedOrigin);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/documents/{Guid.NewGuid()}");
        request.Headers.Add("Origin", AllowedOrigin);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be(AllowedOrigin);
    }

    [Fact]
    public async Task No_configured_origins_disables_cors()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/documents", AllowedOrigin, "POST"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Empty_origin_entries_are_ignored()
    {
        await using var factory = CreateFactory("", AllowedOrigin + "/");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/ask", AllowedOrigin, "POST"));

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be(AllowedOrigin);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://docintake-web.example.net")]
    [InlineData("https://docintake-web.example.net/app")]
    [InlineData("not-a-url")]
    public async Task Invalid_origin_fails_startup(string origin)
    {
        await using var factory = CreateFactory(origin);

        Action start = () => { using var client = factory.CreateClient(); };

        start.Should().Throw<InvalidOperationException>().WithMessage("Cors:AllowedOrigins*");
    }

    [Theory]
    [InlineData("https://example.azurestaticapps.net", true)]
    [InlineData("https://example.com:8443", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://127.0.0.1:5173", true)]
    [InlineData("http://example.com", false)]
    [InlineData("https://*.example.com", false)]
    [InlineData("https://example.com/path", false)]
    [InlineData("https://example.com?x=1", false)]
    [InlineData("https://user@example.com", false)]
    public void Origin_validation(string origin, bool expected) =>
        CorsSettings.IsValidOrigin(origin).Should().Be(expected);

    private static HttpRequestMessage Preflight(string path, string origin, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }

    private static WebApplicationFactory<Program> CreateFactory(params string[] origins) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Development:BypassAuth"] = "false",
                    ["Auth:Authority"] = "https://login.microsoftonline.com/test-tenant/v2.0",
                    ["Auth:Audience"] = "api://docintake-tests",
                    ["Sql:Server"] = "sql.example",
                    ["Sql:Database"] = "docintake",
                    ["Search:Endpoint"] = "https://search.example",
                    ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ""
                };
                for (var i = 0; i < origins.Length; i++)
                {
                    settings[$"Cors:AllowedOrigins:{i}"] = origins[i];
                }

                configuration.AddInMemoryCollection(settings);
            });
        });
}
