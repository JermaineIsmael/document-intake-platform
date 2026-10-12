using DocIntake.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DocIntake.Tests;

public sealed class DocumentStoreConfigurationTests
{
    [Fact]
    public void Development_can_use_the_exact_azurite_endpoint()
    {
        var client = DocumentStore.CreateContainerClient(
            Configuration("http://127.0.0.1:10000/devstoreaccount1"), Environment("Development"));

        client.Uri.AbsoluteUri.Should().Be("http://127.0.0.1:10000/devstoreaccount1/documents");
    }

    [Theory]
    [InlineData("Production", "http://127.0.0.1:10000/devstoreaccount1")]
    [InlineData("Staging", "http://127.0.0.1:10000/devstoreaccount1")]
    [InlineData("Development", "http://storage.example")]
    public void Insecure_non_emulator_endpoints_are_rejected(string environment, string endpoint)
    {
        Action create = () => DocumentStore.CreateContainerClient(Configuration(endpoint), Environment(environment));

        create.Should().Throw<InvalidOperationException>().WithMessage("*HTTPS*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Https_storage_endpoints_use_the_configured_container(string environment)
    {
        var client = DocumentStore.CreateContainerClient(
            Configuration("https://storage.example"), Environment(environment));

        client.Uri.AbsoluteUri.Should().Be("https://storage.example/documents");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-endpoint")]
    public void Missing_or_invalid_endpoint_is_reported_explicitly(string endpoint)
    {
        Action create = () => DocumentStore.CreateContainerClient(Configuration(endpoint), Environment("Production"));

        create.Should().Throw<InvalidOperationException>().WithMessage("Storage:*");
    }

    private static IConfiguration Configuration(string endpoint) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:BlobEndpoint"] = endpoint,
            ["Storage:ContainerName"] = "documents"
        }).Build();

    private static IHostEnvironment Environment(string name) => new TestEnvironment { EnvironmentName = name };

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "DocIntake.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
