using DocIntake.Api.Configuration;
using DocIntake.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

public sealed class FileValidatorTests
{
    private static FileValidator CreateValidator(UploadOptions? options = null) =>
        new(Options.Create(options ?? new UploadOptions()));

    [Fact]
    public async Task Missing_file_is_rejected()
    {
        var result = await CreateValidator().ValidateAsync(null);

        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(400);
        result.File.Should().BeNull();
    }

    [Theory]
    [InlineData("test.pdf", "", 400)]
    [InlineData("test.exe", "%PDF-test", 415)]
    [InlineData("test.pdf", "PK\u0003\u0004test", 415)]
    [InlineData("test.docx", "%PDF-test", 415)]
    [InlineData("test.pdf", "%PDF", 415)]
    [InlineData("test.docx", "PK\u0003", 415)]
    [InlineData("test.pdf", "prefix%PDF-test", 415)]
    public async Task Invalid_files_are_rejected(string name, string content, int status)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var file = new FormFile(stream, 0, stream.Length, "file", name);

        var result = await CreateValidator().ValidateAsync(file);

        result.Error!.StatusCode.Should().Be(status);
        result.File.Should().BeNull();
    }

    [Theory]
    [InlineData("report.pdf", "%PDF-test", "application/pdf")]
    [InlineData("report.PDF", "%PDF-test", "application/pdf")]
    [InlineData("report.DOCX", "PK\u0003\u0004test",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task Signatures_determine_content_type_not_client_headers(
        string name, string content, string expectedContentType)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var file = new FormFile(stream, 0, stream.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/x-untrusted"
        };

        var result = await CreateValidator().ValidateAsync(file);

        result.Error.Should().BeNull();
        result.File!.ContentType.Should().Be(expectedContentType);
    }

    [Theory]
    [InlineData(@"..\folder/unsafe name!é.pdf", "..folderunsafename.pdf")]
    [InlineData(@"C:\folder\a+b.pdf", "Cfolderab.pdf")]
    public async Task Filenames_only_retain_safe_ascii_characters(string name, string sanitizedName)
    {
        using var stream = new MemoryStream("%PDF-test"u8.ToArray());
        var file = new FormFile(stream, 0, stream.Length, "file", name);

        var result = await CreateValidator().ValidateAsync(file);

        result.File!.SanitizedFileName.Should().Be(sanitizedName);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Default_25_MiB_boundary_is_exact(int extraByte, bool valid)
    {
        var options = new UploadOptions();
        using var stream = new MemoryStream("%PDF-test"u8.ToArray());
        var file = new FormFile(stream, 0, options.MaxFileSizeBytes + extraByte, "file", "boundary.pdf");

        var result = await CreateValidator(options).ValidateAsync(file);

        if (valid)
            result.Error.Should().BeNull();
        else
            result.Error!.StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task Configured_allow_list_is_respected()
    {
        using var stream = new MemoryStream("PK\u0003\u0004test"u8.ToArray());
        var file = new FormFile(stream, 0, stream.Length, "file", "test.docx");

        var result = await CreateValidator(new UploadOptions { AllowedExtensions = [".pdf"] })
            .ValidateAsync(file);

        result.Error!.StatusCode.Should().Be(415);
    }

    [Theory]
    [InlineData(0, ".pdf")]
    [InlineData(-1, ".pdf")]
    [InlineData(25, ".exe")]
    public void Invalid_options_are_rejected(int maxFileSizeMb, string extension)
    {
        new UploadOptions { MaxFileSizeMb = maxFileSizeMb, AllowedExtensions = [extension] }
            .IsValid().Should().BeFalse();
    }
}
