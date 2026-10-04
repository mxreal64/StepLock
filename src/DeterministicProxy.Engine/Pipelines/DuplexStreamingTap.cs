using System.Buffers;
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
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        var stopwatch = Stopwatch.StartNew();
        int sequence = 0;

        try
        {
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                var deltaMs = stopwatch.ElapsedMilliseconds;

                // Capture chunk snapshot (independent copy for storage)
                var chunkBytes = new byte[bytesRead];
                buffer.AsSpan(0, bytesRead).CopyTo(chunkBytes);
                chunks.Add(new StreamChunk(sequence++, deltaMs, chunkBytes));

                // Direct write into PipeWriter's managed memory
                var memory = targetWriter.GetMemory(bytesRead);
                buffer.AsSpan(0, bytesRead).CopyTo(memory.Span);
                targetWriter.Advance(bytesRead);

                var flushResult = await targetWriter.FlushAsync(ct);
                if (flushResult.IsCompleted || flushResult.IsCanceled)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return chunks;
    }
}
