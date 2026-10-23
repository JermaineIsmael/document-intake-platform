using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using DocIntake.Api.Configuration;
using DocIntake.Shared;
using DocIntake.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

public sealed class UploadTests
{
    [Fact]
    public async Task Health_returns_200_in_development_without_a_token()
    {
        await using var factory = new UploadFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null, null, 400)]
    [InlineData("test.pdf", "", 400)]
    [InlineData("test.exe", "%PDF-test", 415)]
    [InlineData("test.pdf", "SECRET_FILE_CONTENT", 415)]
    [InlineData("test.docx", "%PDF-test", 415)]
    public async Task Validation_failures_return_json_and_warning_without_uploading(
        string? name, string? body, int statusCode)
    {
        await using var factory = new UploadFactory();
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent();
        if (name is not null)
            content.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body!)), "file", name);

        using var response = await client.PostAsync("/documents", content);

        ((int)response.StatusCode).Should().Be(statusCode);
        var error = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        error.Should().ContainKey("error").WhoseValue.Should().NotBeNullOrWhiteSpace();
        factory.Storage.Uploads.Should().BeEmpty();
        factory.Logs.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("validation failed"));
        factory.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("SECRET_FILE_CONTENT"));
    }

    [Theory]
    [InlineData(@"..\unsafe name!é.PDF", "%PDF-SECRET_FILE_CONTENT", "application/pdf")]
    [InlineData("report.docx", "PK\u0003\u0004SECRET_FILE_CONTENT",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task Upload_stores_exact_bytes_headers_metadata_and_returns_accepted(
        string fileName, string text, string expectedContentType)
    {
        await using var factory = new UploadFactory();
        using var client = factory.CreateClient();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-untrusted");
        content.Add(file, "file", fileName);

        using var response = await client.PostAsync("/documents", content);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<DocumentAccepted>();
        accepted!.DocumentId.Should().NotBeEmpty();
        accepted.Status.Should().Be("queued");
        response.Headers.Location!.OriginalString.Should().Be($"/documents/{accepted.DocumentId:D}");
        var stored = factory.Storage.Uploads.Should().ContainSingle().Subject;
        var sanitizedName = string.Concat(fileName.Where(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'));
        stored.Uri.AbsolutePath.Should().Be($"/documents/{accepted.DocumentId:D}/{sanitizedName}");
        stored.Body.Should().Equal(bytes);
        stored.Headers["x-ms-blob-content-type"].Should().Be(expectedContentType);
        Uri.UnescapeDataString(stored.Headers["x-ms-meta-originalName"]).Should().Be(fileName);
        stored.Headers["x-ms-meta-documentId"].Should().Be(accepted.DocumentId.ToString("D"));
        stored.Headers["x-ms-meta-ownerId"].Should().Be("00000000-0000-0000-0000-000000000001");
        DateTimeOffset.TryParse(stored.Headers["x-ms-meta-createdAt"], out _).Should().BeTrue();
        stored.Headers["If-None-Match"].Should().Be("*");
        factory.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Information
            && entry.Message.Contains(accepted.DocumentId.ToString("D"))
            && entry.Message.Contains(bytes.Length.ToString()));
        factory.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("SECRET_FILE_CONTENT"));
    }

    [Theory]
    [InlineData(0, 202)]
    [InlineData(1, 413)]
    public async Task Multipart_limit_accepts_exact_limit_and_rejects_one_byte_more(int extraByte, int status)
    {
        await using var factory = new UploadFactory(maxFileSizeMb: 1);
        using var client = factory.CreateClient();
        var bytes = new byte[1024 * 1024 + extraByte];
        "%PDF-"u8.CopyTo(bytes);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", "limit.pdf");

        using var response = await client.PostAsync("/documents", content);

        ((int)response.StatusCode).Should().Be(status);
        factory.Storage.Uploads.Count.Should().Be(status == 202 ? 1 : 0);
        if (status == 413)
            (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())
                .Should().ContainKey("error");
    }

    [Fact]
    public async Task Repeated_uploads_get_distinct_document_ids()
    {
        await using var factory = new UploadFactory();
        using var client = factory.CreateClient();
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent("%PDF-test"u8.ToArray()), "file", "same.pdf");
            using var response = await client.PostAsync("/documents", content);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            ids.Add((await response.Content.ReadFromJsonAsync<DocumentAccepted>())!.DocumentId);
        }

        ids.Should().OnlyHaveUniqueItems();
        factory.Storage.Uploads.Should().HaveCount(2);
        factory.Storage.Uploads.Select(upload => upload.Uri.AbsolutePath)
            .Should().BeEquivalentTo(ids.Select(id => $"/documents/{id:D}/same.pdf"));
        var expectedBytes = "%PDF-test"u8.ToArray();
        factory.Storage.Uploads.Should().OnlyContain(upload => upload.Body.SequenceEqual(expectedBytes));
    }

    [Theory]
    [InlineData("wrong-field")]
    [InlineData("two-files")]
    [InlineData("not-multipart")]
    [InlineData("missing-boundary")]
    [InlineData("invalid-media-type")]
    public async Task Invalid_forms_are_rejected(string scenario)
    {
        await using var factory = new UploadFactory();
        using var client = factory.CreateClient();
        using HttpContent content = scenario switch
        {
            "not-multipart" => new StringContent("file"),
            "missing-boundary" => new StringContent("file", System.Text.Encoding.UTF8, "multipart/form-data"),
            "invalid-media-type" => new StringContent("file", System.Text.Encoding.UTF8, "multipart/form-data-invalid"),
            _ => new MultipartFormDataContent()
        };
        if (content is MultipartFormDataContent multipart)
        {
            multipart.Add(new ByteArrayContent("%PDF-test"u8.ToArray()),
                scenario == "wrong-field" ? "document" : "file", "test.pdf");
            if (scenario == "two-files")
                multipart.Add(new ByteArrayContent("%PDF-test"u8.ToArray()), "file", "second.pdf");
        }

        using var response = await client.PostAsync("/documents", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>()).Should().ContainKey("error");
        factory.Storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Blob_failure_is_not_reported_as_accepted()
    {
        await using var factory = new UploadFactory();
        factory.Storage.StatusCode = HttpStatusCode.ServiceUnavailable;
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("%PDF-test"u8.ToArray()), "file", "test.pdf");

        using var response = await client.PostAsync("/documents", content);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.Location.Should().BeNull();
        factory.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);
        factory.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("upload accepted"));
    }

    [Fact]
    public async Task Form_limit_uses_the_configured_byte_limit()
    {
        await using var factory = new UploadFactory(maxFileSizeMb: 1);
        var upload = factory.Services.GetRequiredService<IOptions<UploadOptions>>().Value;
        var form = factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;

        form.MultipartBodyLengthLimit.Should().Be(1024 * 1024);
        upload.MaxRequestBodySize.Should().Be(1024 * 1024 + 64 * 1024);
    }

    [Fact]
    public async Task Configuration_replaces_default_extensions_instead_of_appending_to_them()
    {
        await using var original = new UploadFactory();
        await using var factory = original.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:BypassAuth"] = "true",
                    ["Upload:AllowedExtensions:0"] = ".pdf"
                });
            }));
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("PK\u0003\u0004test"u8.ToArray()), "file", "test.docx");

        using var response = await client.PostAsync("/documents", content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        factory.Services.GetRequiredService<IOptions<UploadOptions>>()
            .Value.AllowedExtensions.Should().Equal(".pdf");
        original.Storage.Uploads.Should().BeEmpty();
    }

    private sealed class UploadFactory(int maxFileSizeMb = 25) : WebApplicationFactory<Program>
    {
        public StorageHandler Storage { get; } = new();
        public LogCollector Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Upload:MaxFileSizeMb"] = maxFileSizeMb.ToString(),
                    ["Development:BypassAuth"] = "true"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<BlobContainerClient>();
                services.AddSingleton(new BlobContainerClient(new Uri("https://storage.example/documents"),
                    new BlobClientOptions
                    {
                        Transport = new HttpClientTransport(Storage),
                        Retry = { MaxRetries = 0 }
                    }));
                services.RemoveAll<ExtractedTextContainerClient>();
                services.AddSingleton(new ExtractedTextContainerClient(new BlobContainerClient(
                    new Uri("https://storage.example/extracted-text"),
                    new BlobClientOptions
                    {
                        Transport = new HttpClientTransport(Storage),
                        Retry = { MaxRetries = 0 }
                    })));
                services.AddSingleton<ILoggerProvider>(Logs);
            });
        }
    }

    private sealed record StoredBlob(Uri Uri, byte[] Body, Dictionary<string, string> Headers);

    private sealed class StorageHandler : HttpMessageHandler
    {
        public ConcurrentQueue<StoredBlob> Uploads { get; } = new();
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key,
                header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            Uploads.Enqueue(new StoredBlob(request.RequestUri!,
                await request.Content!.ReadAsByteArrayAsync(cancellationToken), headers));
            var response = new HttpResponseMessage(StatusCode);
            response.Headers.Add("ETag", "\"test-etag\"");
            response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
            response.Content = new StringContent("");
            return response;
        }
    }

    private sealed class LogCollector : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CollectorLogger(Entries);
        public void Dispose() { }

        private sealed class CollectorLogger(
            ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
