using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeterministicProxy.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeterministicProxy.Tests;

public class EndToEndGatewayTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public EndToEndGatewayTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthEndpoint_ShouldReturnHealthy()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("status").GetString().Should().Be("healthy");
    }

    [Fact]
    public async Task ListSessionsEndpoint_ShouldReturnOk()
    {
        var response = await _client.GetAsync("/api/sessions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.TryGetProperty("total", out var total).Should().BeTrue();
        content.TryGetProperty("sessions", out var sessions).Should().BeTrue();
    }

    [Fact]
    public async Task ForkEndpoint_ShouldDuplicateLineageUpToForkStepAndCreateSession()
    {
        var forkRequest = new
        {
            SessionId = "e2e-session-1",
            SourceBranchId = "main",
            NewBranchId = "experiment-v2",
            ForkAtStepIndex = 2
        };

        var response = await _client.PostAsJsonAsync("/api/sessions/fork", forkRequest);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("message").GetString().Should().Be("BranchCreated");
        content.GetProperty("new_branch_id").GetString().Should().Be("experiment-v2");
    }
}
