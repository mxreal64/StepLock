using System.IO.Pipelines;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Engine.Replay;

public interface IReplayEngine
{
    Task ReplayChunksAsync(
        IReadOnlyList<StreamChunk> chunks,
        PipeWriter targetWriter,
        double speedMultiplier = 0.0, // 0.0 = instant, 1.0 = real-time, >1.0 = accelerated
        CancellationToken ct = default);
}

public sealed class ReplayEngine : IReplayEngine
{
    public static readonly ReplayEngine Instance = new();

    public async Task ReplayChunksAsync(
        IReadOnlyList<StreamChunk> chunks,
        PipeWriter targetWriter,
        double speedMultiplier = 0.0,
        CancellationToken ct = default)
    {
        if (chunks.Count == 0)
            return;

        long previousOffset = 0;

        foreach (var chunk in chunks.OrderBy(c => c.Sequence))
        {
            if (speedMultiplier > 0.0)
            {
                var delayMs = (long)Math.Round((chunk.DeltaMs - previousOffset) / speedMultiplier);
                if (delayMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
                }
                previousOffset = chunk.DeltaMs;
            }

            var memory = targetWriter.GetMemory(chunk.Payload.Length);
            chunk.Payload.CopyTo(memory);
            targetWriter.Advance(chunk.Payload.Length);

            var flushResult = await targetWriter.FlushAsync(ct);
            if (flushResult.IsCompleted || flushResult.IsCanceled)
                break;
        }
    }
}
