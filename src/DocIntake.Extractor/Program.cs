using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var credential = new DefaultAzureCredential();
        var documentIntelligenceEndpoint = context.Configuration["DocIntel:Endpoint"];
        var storageEndpoint = context.Configuration["Storage:BlobEndpoint"];
        if (!Uri.TryCreate(storageEndpoint, UriKind.Absolute, out var blobEndpoint)
            || blobEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Storage__BlobEndpoint must be an absolute HTTPS endpoint.");
        }
        var serviceBusNamespace = context.Configuration["ServiceBus:Namespace"];
        var queueName = context.Configuration["ServiceBus:QueueName"] ?? "extracted-documents";
        if (!Uri.TryCreate(documentIntelligenceEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("DocIntel__Endpoint must be an absolute HTTPS endpoint.");
        }

        if (string.IsNullOrWhiteSpace(serviceBusNamespace)
            || serviceBusNamespace.Contains("://", StringComparison.Ordinal)
            || !serviceBusNamespace.EndsWith(".servicebus.windows.net", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ServiceBus__Namespace must be a fully qualified Azure Service Bus namespace.");
        }

        services.AddSingleton<TokenCredential>(credential);
        services.AddSingleton(_ => new DocumentIntelligenceClient(endpoint, credential));
        services.AddSingleton(_ => new BlobServiceClient(blobEndpoint, credential));
        services.AddSingleton(_ => new ServiceBusClient(serviceBusNamespace, credential));
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<ServiceBusClient>().CreateSender(queueName));
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

await host.RunAsync();
