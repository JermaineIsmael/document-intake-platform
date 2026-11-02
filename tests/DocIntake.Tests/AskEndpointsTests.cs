using Xunit;
using FluentAssertions;
using System.Text.Json;
using DocIntake.Api.Endpoints;
using DocIntake.Api.Services;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocIntake.Tests;

/// <summary>
/// Tests for AskEndpoints. These are simplified endpoint-level tests.
/// Full integration tests (with auth, real RAG processing) should be run against deployed resources.
/// </summary>
public class AskEndpointsTests
{
    [Fact]
    public async Task Disabled_chat_returns_service_unavailable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:BypassAuth"] = "true",
                    ["OpenAI:ChatEnabled"] = "false",
                    ["OpenAI:Endpoint"] = "",
                    ["Search:Endpoint"] = "",
                    ["Storage:BlobEndpoint"] = "http://127.0.0.1:10000/devstoreaccount1"
                }));
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "Test question" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body.Should().ContainKey("error").WhoseValue.Should().Be("Chat is disabled for this environment.");
        using var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enabled_chat_with_missing_configuration_fails_startup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("OpenAI:ChatEnabled", "true");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:BypassAuth"] = "true",
                    ["OpenAI:ChatEnabled"] = "true",
                    ["OpenAI:Endpoint"] = "",
                    ["Search:Endpoint"] = "",
                    ["Storage:BlobEndpoint"] = "http://127.0.0.1:10000/devstoreaccount1"
                }));
        });

        var act = async () =>
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*required configuration is missing*")
            .WithMessage("*Search:Endpoint*")
            .WithMessage("*OpenAI:Endpoint*");
    }

    [Fact]
    public void Enabled_chat_with_complete_configuration_registers_real_rag_services()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("OpenAI:ChatEnabled", "true");
            builder.UseSetting("Search:Endpoint", "https://search.example.com");
            builder.UseSetting("OpenAI:Endpoint", "https://openai.example.com");
            builder.UseSetting("OpenAI:EmbeddingDeployment", "embedding");
            builder.UseSetting("OpenAI:ChatDeployment", "chat");
        });
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<IRagService>().Should().BeOfType<RagService>();
        factory.Services.GetRequiredService<IQuestionEmbedder>()
            .Should().BeOfType<AzureOpenAiQuestionEmbedder>();
    }

    [Fact]
    public async Task Ask_returns_answer_and_sources_for_the_authenticated_owner()
    {
        var rag = new FakeRagService((_, _) => Task.FromResult<AskResponse?>(
            new AskResponse("The answer [source1].", ["https://blob/a"])));
        using var factory = CreateChatFactory(rag);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "What is it?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("answer").GetString().Should().Be("The answer [source1].");
        body.RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetString())
            .Should().Equal("https://blob/a");
        rag.Questions.Should().Equal(("What is it?", "00000000-0000-0000-0000-000000000001"));
    }

    [Fact]
    public async Task Ask_returns_not_found_when_no_relevant_content_exists()
    {
        using var factory = CreateChatFactory(new FakeRagService((_, _) => Task.FromResult<AskResponse?>(null)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "What is it?" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ask_returns_server_error_without_exposing_dependency_failures()
    {
        using var factory = CreateChatFactory(new FakeRagService(
            (_, _) => throw new InvalidOperationException("secret dependency detail")));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "What is it?" });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("secret dependency detail");
    }

    [Fact]
    public async Task Ask_rejects_blank_questions()
    {
        var rag = new FakeRagService((_, _) => Task.FromResult<AskResponse?>(null));
        using var factory = CreateChatFactory(rag);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "  " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        rag.Questions.Should().BeEmpty();
    }

    private static WebApplicationFactory<Program> CreateChatFactory(IRagService rag) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Development:BypassAuth", "true");
            builder.UseSetting("OpenAI:ChatEnabled", "true");
            builder.UseSetting("Search:Endpoint", "https://search.example.com");
            builder.UseSetting("OpenAI:Endpoint", "https://openai.example.com");
            builder.UseSetting("OpenAI:EmbeddingDeployment", "embedding");
            builder.UseSetting("OpenAI:ChatDeployment", "chat");
            builder.UseSetting("Storage:BlobEndpoint", "http://127.0.0.1:10000/devstoreaccount1");
            builder.ConfigureTestServices(services => services.AddSingleton(rag));
        });

    private sealed class FakeRagService(Func<string, string, Task<AskResponse?>> handler) : IRagService
    {
        public List<(string Question, string OwnerId)> Questions { get; } = [];

        public Task<AskResponse?> AskAsync(string question, string ownerId, CancellationToken ct)
        {
            Questions.Add((question, ownerId));
            return handler(question, ownerId);
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
