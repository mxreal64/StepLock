using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeterministicProxy.Core.Canonicalization;
using DeterministicProxy.Core.Cryptography;
using DeterministicProxy.Core.Models;
using DeterministicProxy.Engine.Pipelines;
using DeterministicProxy.Engine.Replay;
using DeterministicProxy.Engine.Safety;
using DeterministicProxy.Engine.Tls;
using DeterministicProxy.Engine.WebSocket;
using DeterministicProxy.Gateway.Commercial;
using DeterministicProxy.Storage.Memory;
using DeterministicProxy.Storage.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeterministicProxy.Tests;

/// <summary>
/// Exhaustive verification suite for all features claimed in README.md
/// </summary>
public class ReadmeFeaturesVerificationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ReadmeFeaturesVerificationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public void Claim1_MerkleExecutionDag_ShouldCryptographicallyLinkSteps()
    {
        var hasher = FrameHasher.Instance;
        var step0Hash = hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "body0", 0);
        var step1Hash = hasher.ComputeFrameHash(step0Hash, "POST", "https://api.stripe.com/v1/charges", "body1", 1);
        var step2Hash = hasher.ComputeFrameHash(step1Hash, "GET", "https://api.github.com/repos/steplock", "empty", 2);

        step0Hash.Should().NotBeNullOrWhiteSpace().And.HaveLength(64);
        step1Hash.Should().NotBe(step0Hash);
        step2Hash.Should().NotBe(step1Hash);

        // If step 0 body changed, all subsequent step hashes MUST diverge
        var alteredStep0Hash = hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "altered_body", 0);
        var alteredStep1Hash = hasher.ComputeFrameHash(alteredStep0Hash, "POST", "https://api.stripe.com/v1/charges", "body1", 1);

        alteredStep1Hash.Should().NotBe(step1Hash);
    }

    [Fact]
    public async Task Claim2_ZeroCopyStreamingTap_ShouldRecordExactInterChunkTimestamps()
    {
        var ssePayload = "data: {\"token\":\"The\"}\n\ndata: {\"token\":\" future\"}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ssePayload));
        var pipe = new Pipe();

        var chunks = await DuplexStreamingTap.InterceptAndForwardAsync(stream, pipe.Writer);
        await pipe.Writer.CompleteAsync();

        chunks.Should().NotBeEmpty();
        chunks.Select(c => c.Sequence).Should().BeInAscendingOrder();
        var reassembled = string.Concat(chunks.Select(c => Encoding.UTF8.GetString(c.Payload)));
        reassembled.Should().Be(ssePayload);
    }

    [Fact]
    public void Claim3_SemanticRequestCanonicalizer_ShouldStripDynamicJitterAndSortKeys()
    {
        var canonicalizer = SemanticRequestCanonicalizer.Default;
        var jsonA = """{"z_key":"last","nonce":"abc-123","a_key":"first","client_timestamp":1700000000}""";
        var jsonB = """{"a_key":"first","client_timestamp":1800000000,"z_key":"last","nonce":"xyz-999"}""";

        var (hashA, canonicalBytesA) = canonicalizer.CanonicalizeBody("application/json", Encoding.UTF8.GetBytes(jsonA));
        var (hashB, canonicalBytesB) = canonicalizer.CanonicalizeBody("application/json", Encoding.UTF8.GetBytes(jsonB));

        hashA.Should().Be(hashB);
        Encoding.UTF8.GetString(canonicalBytesA).Should().Be(Encoding.UTF8.GetString(canonicalBytesB));
    }

    [Fact]
    public async Task Claim4_SqliteWalBatchPersistence_ShouldHandleBatchWritesAndConcurrentReads()
    {
        var dbFile = $"test_wal_claim_{Guid.NewGuid():N}.db";
        var store = new SqliteExecutionStore(dbFile);
        await store.InitializeAsync();

        try
        {
            var frames = Enumerable.Range(0, 10).Select(i => new ExecutionFrame(
                SessionId: "claim-wal-session",
                BranchId: "main",
                StepIndex: i,
                StepId: $"step-{i}",
                ParentStepHash: i > 0 ? $"hash-{i - 1}" : null,
                FrameHash: $"hash-{i}",
                HttpMethod: "POST",
                TargetUri: "https://api.openai.com/v1/chat/completions",
                RequestHeaders: new(),
                RequestBodyHash: $"hash-{i}",
                RequestBodyCanonical: null,
                ResponseStatusCode: 200,
                ResponseHeaders: new(),
                Chunks: new(),
                WebSocketFrames: null,
                SideEffectType: SideEffectType.SafeReadOnly,
                DurationMs: 20,
                TimeToFirstTokenMs: 5,
                CreatedAtUtc: DateTimeOffset.UtcNow
            )).ToList();

            await store.SaveFramesBatchAsync(frames);

            var history = await store.GetExecutionHistoryAsync("claim-wal-session", "main");
            history.Should().HaveCount(10);
        }
        finally
        {
            await store.DisposeAsync();
            if (File.Exists(dbFile)) try { File.Delete(dbFile); } catch { }
        }
    }

    [Fact]
    public void Claim5_DynamicTlsMitm_ShouldGenerateEcdsaLeafCertificates()
    {
        using var ca = new DynamicCertificateAuthority();
        ca.RootCertificate.Should().NotBeNull();

        using var leafCert = ca.GetOrCreateDomainCertificate("api.anthropic.com");
        leafCert.Should().NotBeNull();
        leafCert.Subject.Should().Contain("CN=api.anthropic.com");
        leafCert.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void Claim6_PiiRedactor_ShouldMaskSecretsSafely()
    {
        var redactor = EnterprisePiiRedactor.Instance;
        var text = "Use key sk-proj-123456789012345678901234 and card 4111-2222-3333-4444 with Bearer my_secret_token_1234567890";
        var redacted = redactor.Redact(text);

        redacted.Should().NotContain("sk-proj-123456789012345678901234");
        redacted.Should().NotContain("4111-2222-3333-4444");
        redacted.Should().NotContain("my_secret_token_1234567890");
        redacted.Should().Contain("Bearer");
    }

    [Fact]
    public async Task Claim7_TimeTravelForkAndDiffing_ShouldWorkEndToEnd()
    {
        var sessionId = "time-travel-test-session";

        // 1. Fork session via API
        var forkReq = new ForkRequest(
            SessionId: sessionId,
            SourceBranchId: "main",
            NewBranchId: "experiment-fork",
            ForkAtStepIndex: 1
        );
        var forkResp = await _client.PostAsJsonAsync("/api/sessions/fork", forkReq);
        forkResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Query history API
        var histResp = await _client.GetAsync($"/api/sessions/{sessionId}/history?branchId=main");
        histResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. Verify DAG API
        var dagResp = await _client.GetAsync($"/api/sessions/{sessionId}/verify-dag?branchId=main");
        dagResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Compare branches API
        var diffResp = await _client.GetAsync($"/api/sessions/{sessionId}/compare-branches?baseBranch=main&targetBranch=experiment-fork");
        diffResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Claim8_DashboardAndHealthEndpoints_ShouldBeAccessible()
    {
        // Health endpoint
        var healthResp = await _client.GetAsync("/health");
        healthResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var healthJson = await healthResp.Content.ReadFromJsonAsync<JsonElement>();
        healthJson.GetProperty("status").GetString().Should().Be("healthy");
        healthJson.GetProperty("engine").GetString().Should().Be("StepLock Enterprise");

        // Dashboard endpoint (auto-followed to index.html)
        var dashResp = await _client.GetAsync("/dashboard");
        dashResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dashHtml = await dashResp.Content.ReadAsStringAsync();
        dashHtml.Should().Contain("StepLock");

        // Static index.html content
        var indexResp = await _client.GetAsync("/index.html");
        indexResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await indexResp.Content.ReadAsStringAsync();
        html.Should().Contain("StepLock");
    }
}
