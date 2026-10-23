using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace DocIntake.Worker;

// Explicit Main in the DocIntake.Worker namespace (not top-level statements) so this type does not
// collide with DocIntake.Api's global Program in the test project.
internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var configuration = builder.Configuration;

        var serviceBusNamespace = configuration["ServiceBus:Namespace"];
        if (string.IsNullOrWhiteSpace(serviceBusNamespace)
            || serviceBusNamespace.Contains("://", StringComparison.Ordinal)
            || !serviceBusNamespace.EndsWith(".servicebus.windows.net", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ServiceBus__Namespace must be a fully qualified Azure Service Bus namespace.");
        }

        var searchEndpoint = RequireHttpsEndpoint("Search:Endpoint");
        var openAiEndpoint = RequireHttpsEndpoint("OpenAI:Endpoint");
        var storageEndpoint = RequireHttpsEndpoint("Storage:BlobEndpoint");
        var extractedTextContainer = RequireValue("Storage:ExtractedTextContainer");
        var embeddingDeployment = RequireValue("OpenAI:EmbeddingDeployment");
        var sqlServer = RequireValue("Sql:Server");
        if (sqlServer.Contains(';', StringComparison.Ordinal) || sqlServer.Contains('='))
        {
            throw new InvalidOperationException("Sql__Server must be a server host name, not a connection string.");
        }

        // Server and database only. Authentication is an Entra token from DefaultAzureCredential, so there
        // is no password, user ID, or Authentication keyword in this connection string.
        var sqlConnectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{sqlServer},1433",
            InitialCatalog = RequireValue("Sql:Database"),
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = false,
            ConnectTimeout = 30
        }.ConnectionString;

        var credential = new DefaultAzureCredential();
        builder.Services.AddSingleton<TokenCredential>(credential);
        builder.Services.AddSingleton(_ => new ServiceBusClient(serviceBusNamespace, credential));
        builder.Services.AddSingleton(_ => new AzureOpenAIClient(openAiEndpoint, credential).GetEmbeddingClient(embeddingDeployment));
        builder.Services.AddSingleton(_ => new SearchIndexClient(searchEndpoint, credential));
        builder.Services.AddSingleton(_ => new SearchClient(searchEndpoint, IndexSchema.IndexName, credential));
        builder.Services.AddSingleton(_ => new BlobServiceClient(storageEndpoint, credential)
            .GetBlobContainerClient(extractedTextContainer));
        builder.Services.AddSingleton(_ => new OriginalDocumentsContainer(
            new BlobServiceClient(storageEndpoint, credential)
                .GetBlobContainerClient(configuration["Storage:ContainerName"] ?? "documents")));
        builder.Services.AddSingleton<IExtractedTextReader, BlobExtractedTextReader>();
        var sqlIdentityClientId = configuration["Sql:ManagedIdentityClientId"];
        TokenCredential sqlCredential = string.IsNullOrWhiteSpace(sqlIdentityClientId)
            ? credential
            : Guid.TryParse(sqlIdentityClientId, out _)
                ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(sqlIdentityClientId))
                : throw new InvalidOperationException("Sql__ManagedIdentityClientId must be a GUID.");
        builder.Services.AddSingleton<IDocumentMetadataRepository>(_ => new DocumentMetadataRepository(sqlConnectionString, sqlCredential));
        builder.Services.AddOptions<ChunkIndexerOptions>()
            .Bind(configuration.GetSection(ChunkIndexerOptions.SectionName))
            .Validate(options => options.IsValid(), "Indexing batch sizes must be within the service limits.")
            .ValidateOnStart();
        builder.Services.AddSingleton<IChunkIndexer, ChunkIndexer>();
        builder.Services.AddOptions<IngestionWorkerOptions>()
            .Bind(configuration.GetSection(IngestionWorkerOptions.SectionName))
            .Validate(options => options.IsValid(), "ServiceBus worker settings are invalid: MaxConcurrentCalls 1-64, MaxDeliveryCount >= 1, lock renewal 1-60 minutes.")
            .ValidateOnStart();
        builder.Services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection("Retention"))
            .Validate(options => options.IsValid(), "Retention requires positive day counts, a batch size 1-1000, sweep interval >= 1 minute, and a stale timeout >= 1 hour.")
            .ValidateOnStart();
        builder.Services.AddSingleton<IDocumentRetentionOperations>(services =>
            new BlobSearchRetentionOperations(
                services.GetRequiredService<SearchClient>(),
                services.GetRequiredService<OriginalDocumentsContainer>().Client,
                services.GetRequiredService<BlobContainerClient>()));
        builder.Services.AddHostedService<IngestionWorker>();
        builder.Services.AddOptions<StatusEventWorkerOptions>()
            .Bind(configuration.GetSection(StatusEventWorkerOptions.SectionName))
            .Validate(options => options.IsValid(), "ServiceBus status queue settings are invalid.")
            .ValidateOnStart();
        builder.Services.AddHostedService<StatusEventWorker>();
        builder.Services.AddHostedService<DocumentRetentionWorker>();

        // Same rule as Sprints 1 and 2: export only when APPLICATIONINSIGHTS_CONNECTION_STRING is set.
        // Telemetry authenticates with that connection string, like the Sprint 2 Function, so the worker
        // identity needs no Monitoring Metrics Publisher role beyond the Task 9 assignments.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
        {
            // Azure SDK ActivitySource tracing (Service Bus, Search, Identity) is still opt-in.
            AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddSource("Azure.*"))
                .UseAzureMonitorExporter();
            builder.Services.Configure<OpenTelemetryLoggerOptions>(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });
        }

        builder.Services.AddSingleton(TimeProvider.System);
        var host = builder.Build();
        await host.RunAsync();

        string RequireValue(string key)
        {
            var value = configuration[key];
            return string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException($"{key.Replace(":", "__", StringComparison.Ordinal)} must be configured.")
                : value;
        }

        Uri RequireHttpsEndpoint(string key)
        {
            return Uri.TryCreate(RequireValue(key), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? uri
                : throw new InvalidOperationException($"{key.Replace(":", "__", StringComparison.Ordinal)} must be an absolute HTTPS endpoint.");
        }
    }
}
