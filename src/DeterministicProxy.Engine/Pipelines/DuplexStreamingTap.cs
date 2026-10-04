using System.Diagnostics;
using System.IO.Pipelines;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Engine.Pipelines;

/// <summary>
/// Intercepts duplex streams (SSE, token chunks), tees bytes to the client PipeWriter with
/// zero added latency, and collects timestamped chunks for Merkle DAG storage.
/// </summary>
public sealed class DuplexStreamingTap
{
    public static async Task<List<StreamChunk>> InterceptAndForwardAsync(
        Stream sourceStream,
        PipeWriter targetWriter,
        CancellationToken ct = default)
    {
        var chunks = new List<StreamChunk>();
        var stopwatch = Stopwatch.StartNew();
        int sequence = 0;

        while (true)
        {
            var memory = targetWriter.GetMemory(16384);
            int bytesRead = await sourceStream.ReadAsync(memory, ct).ConfigureAwait(false);
            if (bytesRead <= 0)
                break;

            var deltaMs = stopwatch.ElapsedMilliseconds;

            var chunkBytes = GC.AllocateUninitializedArray<byte>(bytesRead);
            memory.Span.Slice(0, bytesRead).CopyTo(chunkBytes);
            chunks.Add(new StreamChunk(sequence++, deltaMs, chunkBytes));

            targetWriter.Advance(bytesRead);

            var flushResult = await targetWriter.FlushAsync(ct).ConfigureAwait(false);
            if (flushResult.IsCompleted || flushResult.IsCanceled)
                break;
        }

        return chunks;
    }
}
