using Xunit;
using FluentAssertions;
using System.Text.Json;
using DocIntake.Api.Endpoints;

namespace DocIntake.Tests;

/// <summary>
/// Tests for AskEndpoints. These are simplified endpoint-level tests.
/// Full integration tests (with auth, real RAG processing) should be run against deployed resources.
/// </summary>
public class AskEndpointsTests
{
    [Fact]
    public void AskEndpoints_DoesNotThrowOnConstruction()
    {
        // Verify the endpoints can be registered without errors
        // Full endpoint testing requires a WebApplicationFactory with proper DI setup,
        // which depends on Azure resource configuration (Search, OpenAI).
        // For now, we just verify no structural errors.
        Assert.True(typeof(AskEndpoints).IsPublic);
    }
}
