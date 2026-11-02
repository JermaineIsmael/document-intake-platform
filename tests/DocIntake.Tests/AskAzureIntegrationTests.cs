using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DocIntake.Tests;

/// <summary>
/// Opt-in checks against a deployed API with chat enabled and a document already indexed for user A.
/// Skipped unless ASK_IT_BASE_URL, ASK_IT_TOKEN_A, ASK_IT_QUESTION and ASK_IT_EXPECTED_SOURCE are set.
/// ASK_IT_TOKEN_B (a different user) enables the cross-owner isolation check.
/// See docs/runbooks/ask-verification.md.
/// </summary>
public sealed class AskAzureIntegrationTests
{
    private static string? Setting(string name) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    private static HttpClient CreateClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(Setting("ASK_IT_BASE_URL")!), Timeout = TimeSpan.FromSeconds(90) };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    [AzureIntegrationFact("ASK_IT_BASE_URL")]
    public async Task Unauthenticated_request_is_rejected()
    {
        using var client = CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "What is this about?" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [AzureIntegrationFact("ASK_IT_BASE_URL", "ASK_IT_TOKEN_A")]
    public async Task Blank_question_is_rejected()
    {
        using var client = CreateClient(Setting("ASK_IT_TOKEN_A"));

        using var response = await client.PostAsJsonAsync("/ask", new { question = " " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [AzureIntegrationFact("ASK_IT_BASE_URL", "ASK_IT_TOKEN_A", "ASK_IT_QUESTION", "ASK_IT_EXPECTED_SOURCE")]
    public async Task Owner_receives_a_grounded_answer_citing_their_document()
    {
        using var client = CreateClient(Setting("ASK_IT_TOKEN_A"));

        using var response = await client.PostAsJsonAsync("/ask", new { question = Setting("ASK_IT_QUESTION") });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "chat must be enabled and the document indexed for user A");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("answer").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetString())
            .Should().Contain(source => source!.Contains(Setting("ASK_IT_EXPECTED_SOURCE")!, StringComparison.OrdinalIgnoreCase));
    }

    [AzureIntegrationFact("ASK_IT_BASE_URL", "ASK_IT_TOKEN_B", "ASK_IT_QUESTION", "ASK_IT_EXPECTED_SOURCE")]
    public async Task Other_user_never_receives_the_owners_document()
    {
        using var client = CreateClient(Setting("ASK_IT_TOKEN_B"));

        using var response = await client.PostAsJsonAsync("/ask", new { question = Setting("ASK_IT_QUESTION") });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.OK);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetString())
                .Should().NotContain(source => source!.Contains(Setting("ASK_IT_EXPECTED_SOURCE")!, StringComparison.OrdinalIgnoreCase));
        }
    }

    [AzureIntegrationFact("ASK_IT_BASE_URL", "ASK_IT_TOKEN_A", "ASK_IT_UNRELATED_QUESTION")]
    public async Task Unrelated_question_does_not_fabricate_an_answer()
    {
        using var client = CreateClient(Setting("ASK_IT_TOKEN_A"));

        using var response = await client.PostAsJsonAsync("/ask", new { question = Setting("ASK_IT_UNRELATED_QUESTION") });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.OK);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("answer").GetString().Should().Contain("don't have enough information");
        }
    }
}

public sealed class AzureIntegrationFactAttribute : FactAttribute
{
    public AzureIntegrationFactAttribute(params string[] requiredSettings)
    {
        var missing = requiredSettings.Where(n => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))).ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Azure integration check skipped; set {string.Join(", ", missing)}.";
        }
    }
}
