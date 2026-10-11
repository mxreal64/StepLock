using System.IO.Pipelines;
using DeterministicProxy.Core.Models;
using DeterministicProxy.Engine.Replay;

namespace DeterministicProxy.Engine.Replay;

public interface IVirtualTimingEngine : IReplayEngine
{
    Task ReplayWithJitterSimulationAsync(
        IReadOnlyList<StreamChunk> chunks,
        PipeWriter targetWriter,
        double speedMultiplier = 1.0,
        double jitterVarianceMs = 5.0,
        CancellationToken ct = default);
}

public sealed class VirtualTimingEngine : IVirtualTimingEngine
{
    private readonly Random _random = new();

    public Task ReplayChunksAsync(
        IReadOnlyList<StreamChunk> chunks,
        PipeWriter targetWriter,
        double speedMultiplier = 0.0,
        CancellationToken ct = default)
    {
        return ReplayEngine.Instance.ReplayChunksAsync(chunks, targetWriter, speedMultiplier, ct);
    }

    public async Task ReplayWithJitterSimulationAsync(
        IReadOnlyList<StreamChunk> chunks,
        PipeWriter targetWriter,
        double speedMultiplier = 1.0,
        double jitterVarianceMs = 5.0,
        CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        long previousOffset = 0;
        foreach (var chunk in chunks.OrderBy(c => c.Sequence))
        {
            if (speedMultiplier > 0.0)
            {
                var baseDelay = (chunk.DeltaMs - previousOffset) / speedMultiplier;
                var jitter = (_random.NextDouble() * 2 - 1) * jitterVarianceMs;
                var delayMs = (long)Math.Max(0, Math.Round(baseDelay + jitter));

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
            if (flushResult.IsCompleted || flushResult.IsCanceled) break;
        }
    }
}
