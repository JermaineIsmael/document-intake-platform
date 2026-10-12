using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using DocIntake.Api.Services;
using DocIntake.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DocIntake.Tests;

public sealed class AuthenticationTests
{
    private const string Issuer = "https://login.microsoftonline.com/test-tenant/v2.0";
    private const string Audience = "api://docintake-tests";
    private static readonly SymmetricSecurityKey SigningKey =
        new(RandomNumberGenerator.GetBytes(64));

    [Theory]
    [InlineData("Development", true, HttpStatusCode.OK)]
    [InlineData("Development", false, HttpStatusCode.Unauthorized)]
    [InlineData("Production", true, HttpStatusCode.Unauthorized)]
    [InlineData("Production", false, HttpStatusCode.Unauthorized)]
    [InlineData("Staging", true, HttpStatusCode.Unauthorized)]
    public async Task Bypass_is_only_effective_in_development(
        string environment, bool bypass, HttpStatusCode expectedStatus)
    {
        await using var factory = CreateFactory(environment, bypass);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(expectedStatus);
        if (expectedStatus == HttpStatusCode.OK)
        {
            var status = await response.Content.ReadFromJsonAsync<ServiceStatus>();
            status.Should().Be(new ServiceStatus("DocIntake.Api"));
        }
        else
        {
            response.Headers.WwwAuthenticate.Should().ContainSingle()
                .Which.Scheme.Should().Be("Bearer");
        }
    }

    [Theory]
    [InlineData(Audience, Issuer, false, HttpStatusCode.OK)]
    [InlineData("api://wrong-audience", Issuer, false, HttpStatusCode.Unauthorized)]
    [InlineData(Audience, "https://wrong-issuer.example", false, HttpStatusCode.Unauthorized)]
    [InlineData(Audience, Issuer, true, HttpStatusCode.Unauthorized)]
    public async Task Enforced_auth_validates_token(
        string audience, string issuer, bool expired, HttpStatusCode expectedStatus)
    {
        await using var factory = CreateFactory("Production", true);
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [new Claim("sub", "test-user")],
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));

        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task Malformed_bearer_token_is_rejected()
    {
        await using var factory = CreateFactory("Production", false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_with_an_untrusted_signature_is_rejected()
    {
        await using var factory = CreateFactory("Production", false);
        using var client = factory.CreateClient();
        var untrustedKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(untrustedKey, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));

        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    public async Task Health_is_anonymous_when_auth_is_enforced(string path)
    {
        await using var factory = CreateFactory("Production", false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", true)]
    [InlineData("Staging", false)]
    [InlineData("Development", false)]
    public async Task Upload_requires_authorization(string environment, bool bypass)
    {
        await using var factory = CreateFactory(environment, bypass);
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("%PDF-test"u8.ToArray()), "file", "test.pdf");

        using var response = await client.PostAsync("/documents", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    public async Task Bypass_handler_is_registered_only_for_opted_in_development(
        string environment, bool bypass, bool expectedRegistration)
    {
        await using var factory = CreateFactory(environment, bypass);

        factory.Services.GetServices<IAuthorizationHandler>()
            .OfType<AllowAllAuthorizationHandler>().Any().Should().Be(expectedRegistration);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("expired")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-signature")]
    public async Task Upload_rejects_invalid_tokens_outside_development(string scenario)
    {
        await using var factory = CreateFactory("Production", true);
        using var client = factory.CreateClient();
        var token = scenario == "not-a-jwt" ? scenario : new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(
                scenario == "wrong-issuer" ? "https://wrong-issuer.example" : Issuer,
                scenario == "wrong-audience" ? "api://wrong-audience" : Audience,
                notBefore: DateTime.UtcNow.AddHours(-2),
                expires: scenario == "expired" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(
                    scenario == "wrong-signature" ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)) : SigningKey,
                    SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("%PDF-test"u8.ToArray()), "file", "test.pdf");

        using var response = await client.PostAsync("/documents", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Development_defaults_point_to_azurite_and_enable_bypass()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        configuration.GetValue<bool>("Development:BypassAuth").Should().BeTrue();
        configuration["Storage:BlobEndpoint"].Should()
            .Be("http://127.0.0.1:10000/devstoreaccount1");

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task Missing_auth_configuration_fails_startup(string environment, bool bypass)
    {
        await using var factory = CreateFactory(environment, bypass, configureAuth: false);

        Action start = () => { using var client = factory.CreateClient(); };

        start.Should().Throw<InvalidOperationException>().WithMessage("Auth:Authority*");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string environment, bool bypass, bool configureAuth = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Development:BypassAuth", bypass.ToString());
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:BypassAuth"] = bypass.ToString(),
                    ["Auth:Authority"] = configureAuth ? Issuer : "",
                    ["Auth:Audience"] = configureAuth ? Audience : "",
                    ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ""
                }));
            builder.ConfigureServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                    metadata.SigningKeys.Add(SigningKey);
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                }));
        });
}
