using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using DocIntake.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DocIntake.Tests;

// /ask behaviour with authentication enforced (Production, no bypass) and real signed JWTs.
public sealed class AskAuthorizationTests
{
    private const string Issuer = "https://login.microsoftonline.com/test-tenant/v2.0";
    private const string Audience = "api://docintake-tests";
    private const string OwnerA = "11111111-1111-1111-1111-111111111111";
    private const string OwnerB = "22222222-2222-2222-2222-222222222222";
    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(64));

    [Fact]
    public async Task Ask_without_a_token_is_unauthorized_and_does_not_invoke_rag()
    {
        var rag = new RecordingRagService();
        await using var factory = CreateFactory(rag);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "What?" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        rag.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("api://wrong-audience", Issuer)]
    [InlineData(Audience, "https://wrong-issuer.example")]
    public async Task Ask_with_an_invalid_token_is_unauthorized(string audience, string issuer)
    {
        var rag = new RecordingRagService();
        await using var factory = CreateFactory(rag);
        using var client = ClientWithToken(factory, CreateToken(OwnerA, audience, issuer));

        using var response = await client.PostAsJsonAsync("/ask", new { question = "What?" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        rag.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task Ask_without_a_usable_owner_claim_is_forbidden(string? oid)
    {
        var rag = new RecordingRagService();
        await using var factory = CreateFactory(rag);
        using var client = ClientWithToken(factory, CreateToken(oid));

        using var response = await client.PostAsJsonAsync("/ask", new { question = "What?" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        rag.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Ask_scopes_each_request_to_the_callers_own_oid()
    {
        var rag = new RecordingRagService();
        await using var factory = CreateFactory(rag);

        foreach (var owner in new[] { OwnerA, OwnerB })
        {
            using var client = ClientWithToken(factory, CreateToken(owner));
            using var response = await client.PostAsJsonAsync("/ask", new { question = "What?" });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        rag.Calls.Should().Equal(("What?", OwnerA), ("What?", OwnerB));
    }

    [Fact]
    public async Task Ask_ignores_an_owner_supplied_in_the_request_body()
    {
        var rag = new RecordingRagService();
        await using var factory = CreateFactory(rag);
        using var client = ClientWithToken(factory, CreateToken(OwnerA));

        using var response = await client.PostAsJsonAsync("/ask", new { question = "What?", ownerId = OwnerB });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rag.Calls.Should().Equal(("What?", OwnerA));
    }

    [Fact]
    public async Task Disabled_chat_still_requires_authentication()
    {
        var rag = new RecordingRagService();
        await using var factory = CreateFactory(rag, chatEnabled: false);

        using var anonymous = factory.CreateClient();
        using var anonymousResponse = await anonymous.PostAsJsonAsync("/ask", new { question = "What?" });
        using var authenticated = ClientWithToken(factory, CreateToken(OwnerA));
        using var authenticatedResponse = await authenticated.PostAsJsonAsync("/ask", new { question = "What?" });

        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        authenticatedResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        rag.Calls.Should().BeEmpty();
    }

    private static HttpClient ClientWithToken(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string CreateToken(string? oid, string audience = Audience, string issuer = Issuer)
    {
        var claims = new List<Claim> { new("sub", "test-user") };
        if (oid is not null)
        {
            claims.Add(new Claim("oid", oid));
        }

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer, audience, claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));
    }

    private static WebApplicationFactory<Program> CreateFactory(IRagService rag, bool chatEnabled = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Authority"] = Issuer,
                    ["Auth:Audience"] = Audience,
                    ["Sql:Server"] = "sql.example",
                    ["Sql:Database"] = "docintake",
                    ["Search:Endpoint"] = "https://search.example.com",
                    ["OpenAI:Endpoint"] = "https://openai.example.com",
                    ["OpenAI:EmbeddingDeployment"] = "embedding",
                    ["OpenAI:ChatDeployment"] = "chat",
                    ["OpenAI:ChatEnabled"] = chatEnabled.ToString(),
                    ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ""
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(rag);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                    metadata.SigningKeys.Add(SigningKey);
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                });
            });
        });

    private sealed class RecordingRagService : IRagService
    {
        public List<(string Question, string OwnerId)> Calls { get; } = [];

        public Task<AskResponse?> AskAsync(string question, string ownerId, CancellationToken ct)
        {
            Calls.Add((question, ownerId));
            return Task.FromResult<AskResponse?>(new AskResponse("Answer", ["https://blob/a"]));
        }
    }
}
