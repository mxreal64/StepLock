using System.Collections.Concurrent;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Gateway.Commercial;

public sealed class UsageMeteringService : IUsageMeteringService
{
    private readonly ConcurrentDictionary<string, TenantUsageCounters> _meters = new();

    public void RecordStep(string tenantId, bool isReplay, long bytesTransferred, long durationMs)
    {
        var counters = _meters.GetOrAdd(tenantId, _ => new TenantUsageCounters());
        if (isReplay)
        {
            Interlocked.Increment(ref counters.ReplayedSteps);
            // Rough estimate: ~$0.01 saving per cached LLM step replayed
            Interlocked.Add(ref counters.EstimatedCostSavedCents, 1);
        }
        else
        {
            Interlocked.Increment(ref counters.RecordedSteps);
        }
        Interlocked.Add(ref counters.ProxiedBytes, bytesTransferred);
    }

    public TenantUsageMetrics GetUsage(string tenantId)
    {
        if (_meters.TryGetValue(tenantId, out var counters))
        {
            return new TenantUsageMetrics(
                TotalRecordedSteps: Interlocked.Read(ref counters.RecordedSteps),
                TotalReplayedSteps: Interlocked.Read(ref counters.ReplayedSteps),
                TotalProxiedBytes: Interlocked.Read(ref counters.ProxiedBytes),
                EstimatedLlmCostSavedCents: Interlocked.Read(ref counters.EstimatedCostSavedCents)
            );
        }
        return new TenantUsageMetrics(0, 0, 0, 0);
    }

    private sealed class TenantUsageCounters
    {
        public long RecordedSteps;
        public long ReplayedSteps;
        public long ProxiedBytes;
        public long EstimatedCostSavedCents;
    }
}
