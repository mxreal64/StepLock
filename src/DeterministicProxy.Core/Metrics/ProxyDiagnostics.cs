using System.Diagnostics.Metrics;

namespace DeterministicProxy.Core.Metrics;

public static class ProxyDiagnostics
{
    public const string MeterName = "DeterministicProxy.Gateway";
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> RecordedStepsCounter = Meter.CreateCounter<long>(
        "proxy_recorded_steps_total",
        description: "Total number of steps recorded live");

    public static readonly Counter<long> ReplayedStepsCounter = Meter.CreateCounter<long>(
        "proxy_replayed_steps_total",
        description: "Total number of steps served deterministically from cache");

    public static readonly Counter<long> ShortCircuitedMutationsCounter = Meter.CreateCounter<long>(
        "proxy_short_circuited_mutations_total",
        description: "Total number of mutating side-effects safely virtualized/short-circuited");

    public static readonly Histogram<double> StepLatencyHistogram = Meter.CreateHistogram<double>(
        "proxy_step_duration_ms",
        unit: "ms",
        description: "Latency of execution steps in milliseconds");

    public static readonly Histogram<double> TimeToFirstTokenHistogram = Meter.CreateHistogram<double>(
        "proxy_ttft_ms",
        unit: "ms",
        description: "Time to First Token (TTFT) for streaming responses in milliseconds");
}
