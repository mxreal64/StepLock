using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using DeterministicProxy.Engine.Pipelines;
using DeterministicProxy.Engine.Replay;
using FluentAssertions;
using Xunit;

namespace DeterministicProxy.Tests;

public class StreamingAndReplayTests
{
    [Fact]
    public async Task DuplexStreamingTap_ShouldCaptureAllChunksAndForwardToPipe()
    {
        var rawData = "data: {\"token\": \"Hello\"}\n\ndata: {\"token\": \" world!\"}\n\n";
        using var sourceStream = new MemoryStream(Encoding.UTF8.GetBytes(rawData));

        var pipe = new Pipe();

        // Act: Run tap
        var chunks = await DuplexStreamingTap.InterceptAndForwardAsync(sourceStream, pipe.Writer);
        await pipe.Writer.CompleteAsync();

        // Read pipe content
        var readResult = await pipe.Reader.ReadAsync();
        var clientReceived = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        // Assert
        clientReceived.Should().Be(rawData);
        chunks.Should().NotBeEmpty();

        var reassembled = string.Concat(chunks.Select(c => Encoding.UTF8.GetString(c.Payload)));
        reassembled.Should().Be(rawData);
    }

    [Fact]
    public async Task DuplexStreamingTap_EmptyStream_ShouldReturnEmptyChunks()
    {
        using var emptyStream = new MemoryStream(Array.Empty<byte>());
        var pipe = new Pipe();
        var chunks = await DuplexStreamingTap.InterceptAndForwardAsync(emptyStream, pipe.Writer);
        await pipe.Writer.CompleteAsync();
        chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplayEngine_ShouldReplayRecordedChunksAccurately()
    {
        var rawData = "data: {\"result\": \"success\"}\n\n";
        var chunks = new[]
        {
            new Core.Models.StreamChunk(0, 5, Encoding.UTF8.GetBytes("data: ")),
            new Core.Models.StreamChunk(1, 15, Encoding.UTF8.GetBytes("{\"result\": \"success\"}\n\n"))
        };

        var pipe = new Pipe();

        // Act
        await ReplayEngine.Instance.ReplayChunksAsync(chunks, pipe.Writer, speedMultiplier: 0.0);
        await pipe.Writer.CompleteAsync();

        var readResult = await pipe.Reader.ReadAsync();
        var clientReceived = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        clientReceived.Should().Be(rawData);
    }

    [Fact]
    public async Task ReplayEngine_EmptyChunks_ShouldCompleteWithoutError()
    {
        var pipe = new Pipe();
        await ReplayEngine.Instance.ReplayChunksAsync(Array.Empty<Core.Models.StreamChunk>(), pipe.Writer, speedMultiplier: 0.0);
        await pipe.Writer.CompleteAsync();
        var result = await pipe.Reader.ReadAsync();
        result.Buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task ReplayEngine_ShouldRespectChunkOrdering()
    {
        var chunks = new[]
        {
            new Core.Models.StreamChunk(2, 30, Encoding.UTF8.GetBytes("C")),
            new Core.Models.StreamChunk(0, 5, Encoding.UTF8.GetBytes("A")),
            new Core.Models.StreamChunk(1, 15, Encoding.UTF8.GetBytes("B")),
        };
        var pipe = new Pipe();
        await ReplayEngine.Instance.ReplayChunksAsync(chunks, pipe.Writer, speedMultiplier: 0.0);
        await pipe.Writer.CompleteAsync();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.ToArray());
        output.Should().Be("ABC");
    }
}
