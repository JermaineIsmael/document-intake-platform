using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;

namespace DocIntake.Tests;

public sealed class KestrelUploadLimitTests(RunningApi api) : IClassFixture<RunningApi>
{
    [Theory]
    [InlineData(0, false, 415)]
    [InlineData(0, true, 415)]
    [InlineData(1, false, 413)]
    [InlineData(1, true, 413)]
    [InlineData(128 * 1024, false, 413)]
    [InlineData(128 * 1024, true, 413)]
    public async Task Real_server_enforces_multipart_and_request_limits(
        int extraBytes, bool chunked, int expectedStatus)
    {
        var bytes = new byte[1024 * 1024 + extraBytes];
        // Invalid signature avoids storage I/O and proves exactly-at-limit requests reach validation.
        "invalid"u8.CopyTo(bytes);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(bytes), "file", "limit.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/documents");
        request.Content = chunked ? new ChunkedContent(multipart) : multipart;
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.ExpectContinue = true;

        using var response = await api.Client.SendAsync(request);

        ((int)response.StatusCode).Should().Be(expectedStatus);
        var error = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        error.Should().ContainKey("error");
        if (expectedStatus == 415)
            error!["error"].Should().Be("The file content does not match its extension.");
        else
            error!["error"].Should().Contain("size limit");
    }

    [Fact]
    public async Task Health_is_anonymous_on_the_real_server()
    {
        using var response = await api.Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregate_body_limit_applies_even_when_each_section_is_below_the_file_limit(bool chunked)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(new byte[600 * 1024]), "file", "first.pdf");
        multipart.Add(new ByteArrayContent(new byte[600 * 1024]), "file", "second.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/documents")
        {
            Content = chunked ? new ChunkedContent(multipart) : multipart
        };
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.ExpectContinue = true;

        using var response = await api.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var error = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        error!["error"].Should().Be("The request exceeds the configured size limit.");
    }

    private sealed class ChunkedContent : HttpContent
    {
        private readonly HttpContent content;

        public ChunkedContent(HttpContent content)
        {
            this.content = content;
            foreach (var header in content.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            content.CopyToAsync(stream);

    }
}

public sealed class RunningApi : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> output = new();
    private readonly TaskCompletionSource<Uri> listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? process;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocIntake.sln")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Cannot locate the solution root for the Kestrel test.");

        var configuration = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var apiDirectory = Path.Combine(directory.FullName, "src", "DocIntake.Api");
        var assemblyPath = Path.Combine(apiDirectory, "bin", configuration, "net8.0", "DocIntake.Api.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            assemblyPath, "--urls", "http://127.0.0.1:0",
            "--Upload:MaxFileSizeMb", "1", "--Development:BypassAuth", "true"
        })
            start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "";
        process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not { } line)
                return;
            output.Enqueue(line);
            const string marker = "Now listening on: ";
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0 && Uri.TryCreate(line[(markerIndex + marker.Length)..].Trim(),
                UriKind.Absolute, out var uri))
                listening.TrySetResult(uri);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
                output.Enqueue(line);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var uri = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = uri,
                Timeout = TimeSpan.FromSeconds(15)
            };
            using var health = await Client.GetAsync("/health");
            health.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (exception is TimeoutException or HttpRequestException)
        {
            await DisposeAsync();
            throw new InvalidOperationException($"Kestrel startup failed: {string.Join(Environment.NewLine, output)}",
                exception);
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (process is not null)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
            process = null;
        }
    }
}
