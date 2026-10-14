using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using DocIntake.Api.Configuration;
using DocIntake.Api.Endpoints;
using DocIntake.Api.Services;
using DocIntake.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using OpenTelemetry.Logs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Auth:Authority"];
        options.Audience = builder.Configuration["Auth:Audience"];
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
if (builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("Development:BypassAuth"))
{
    builder.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
}
builder.Services.AddHealthChecks();
builder.Services.AddOptions<UploadOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        var section = configuration.GetSection(UploadOptions.SectionName);
        options.MaxFileSizeMb = section.GetValue(nameof(UploadOptions.MaxFileSizeMb), options.MaxFileSizeMb);
        options.AllowedExtensions = section.GetSection(nameof(UploadOptions.AllowedExtensions))
            .Get<string[]>() ?? options.AllowedExtensions;
    })
    .Validate(options => options.IsValid(), "Upload requires a positive MaxFileSizeMb and PDF/DOCX allowed extensions.")
    .ValidateOnStart();
builder.Services.AddOptions<FormOptions>().Configure<IOptions<UploadOptions>>((form, upload) =>
    form.MultipartBodyLengthLimit = upload.Value.MaxFileSizeBytes);
builder.Services.AddOptions<KestrelServerOptions>().Configure<IOptions<UploadOptions>>((kestrel, upload) =>
    kestrel.Limits.MaxRequestBodySize = upload.Value.MaxRequestBodySize);
builder.Services.AddSingleton<FileValidator>();
builder.Services.AddSingleton<BlobContainerClient>(services =>
    DocumentStore.CreateContainerClient(services.GetRequiredService<IConfiguration>(),
        services.GetRequiredService<IHostEnvironment>()));
builder.Services.AddSingleton<DocumentStore>();

// Sprint 4: RAG service for /ask endpoint.
var searchEndpoint = builder.Configuration["Search:Endpoint"];
var openAiEndpoint = builder.Configuration["OpenAI:Endpoint"];
var embeddingDeployment = builder.Configuration["OpenAI:EmbeddingDeployment"];
var chatDeployment = builder.Configuration["OpenAI:ChatDeployment"];
const string SearchIndexName = "document-chunks"; // Must match IndexSchema.IndexName in Worker

// Register RAG service only if all config values are present
var hasRagConfig = !string.IsNullOrWhiteSpace(searchEndpoint)
    && !string.IsNullOrWhiteSpace(openAiEndpoint)
    && !string.IsNullOrWhiteSpace(embeddingDeployment)
    && !string.IsNullOrWhiteSpace(chatDeployment);

if (hasRagConfig)
{
    if (!Uri.TryCreate(searchEndpoint, UriKind.Absolute, out var searchUri) || searchUri?.Scheme != Uri.UriSchemeHttps)
    {
        throw new InvalidOperationException("Search:Endpoint must be an absolute HTTPS URL.");
    }

    if (!Uri.TryCreate(openAiEndpoint, UriKind.Absolute, out var openAiUri) || openAiUri?.Scheme != Uri.UriSchemeHttps)
    {
        throw new InvalidOperationException("OpenAI:Endpoint must be an absolute HTTPS URL.");
    }

    var credential = new DefaultAzureCredential();
    builder.Services.AddSingleton(_ => new AzureOpenAIClient(openAiUri, credential));
    builder.Services.AddSingleton(_ => new SearchClient(new Uri(searchEndpoint!), SearchIndexName, credential));
    builder.Services.AddSingleton(services =>
        services.GetRequiredService<AzureOpenAIClient>().GetEmbeddingClient(embeddingDeployment));
    builder.Services.AddSingleton(services =>
        services.GetRequiredService<AzureOpenAIClient>().GetChatClient(chatDeployment));
    builder.Services.AddSingleton<IRagService, RagService>();
}
else
{
    // Register null RAG service for tests that don't provide config
    builder.Services.AddSingleton<IRagService>(_ => new NullRagService());
}

if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(options =>
    {
        options.Credential = new DefaultAzureCredential();
    });
    builder.Services.Configure<OpenTelemetryLoggerOptions>(options =>
    {
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
    });
}

var app = builder.Build();
var bypassAuth = app.Environment.IsDevelopment()
    && app.Configuration.GetValue<bool>("Development:BypassAuth");
if (bypassAuth)
{
    app.Logger.LogWarning("Authentication enforcement is bypassed for local Development only.");
}
else
{
    if (!Uri.TryCreate(app.Configuration["Auth:Authority"], UriKind.Absolute, out var authority)
        || authority.Scheme != Uri.UriSchemeHttps
        || string.IsNullOrWhiteSpace(app.Configuration["Auth:Audience"]))
    {
        throw new InvalidOperationException(
            "Auth:Authority must be an absolute HTTPS URL and Auth:Audience must be set when authentication is enforced.");
    }
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapGet("/", () => new ServiceStatus("DocIntake.Api")).RequireAuthorization();
app.MapDocumentEndpoints();
app.MapAskEndpoints();

app.Run();

public partial class Program { }
