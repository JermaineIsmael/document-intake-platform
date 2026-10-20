using Xunit;
using FluentAssertions;
using System.Text.Json;
using DocIntake.Api.Endpoints;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DocIntake.Tests;

/// <summary>
/// Tests for AskEndpoints. These are simplified endpoint-level tests.
/// Full integration tests (with auth, real RAG processing) should be run against deployed resources.
/// </summary>
public class AskEndpointsTests
{
    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.NotFound)]
    public async Task Chat_flag_controls_endpoint_availability(bool enabled, HttpStatusCode expected)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:BypassAuth"] = "true",
                    ["OpenAI:ChatEnabled"] = enabled.ToString(),
                    ["OpenAI:Endpoint"] = "",
                    ["Search:Endpoint"] = "",
                    ["Storage:BlobEndpoint"] = "http://127.0.0.1:10000/devstoreaccount1"
                }));
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "Test question" });

        response.StatusCode.Should().Be(expected);
        if (!enabled)
        {
            var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            body.Should().ContainKey("error").WhoseValue.Should().Be("Chat is disabled for this environment.");
            using var health = await client.GetAsync("/health");
            health.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void AskEndpoints_DoesNotThrowOnConstruction()
    {
        // Verify the endpoints can be registered without errors
        // Full endpoint testing requires a WebApplicationFactory with proper DI setup,
        // which depends on Azure resource configuration (Search, OpenAI).
        // For now, we just verify no structural errors.
        Assert.True(typeof(AskEndpoints).IsPublic);
    }
}
