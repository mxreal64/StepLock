using System.Threading.Channels;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeterministicProxy.Engine.Wal;

public interface IWalQueue
{
    ValueTask EnqueueFrameAsync(ExecutionFrame frame, CancellationToken ct = default);
}

public sealed class BackgroundWalQueue : BackgroundService, IWalQueue
{
    private readonly Channel<ExecutionFrame> _channel;
    private readonly IExecutionStore _store;
    private readonly ILogger<BackgroundWalQueue> _logger;
    private const int BatchSize = 50;

    public BackgroundWalQueue(IExecutionStore store, ILogger<BackgroundWalQueue> logger, int capacity = 10000)
    {
        _store = store;
        _logger = logger;
        _channel = Channel.CreateBounded<ExecutionFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async ValueTask EnqueueFrameAsync(ExecutionFrame frame, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(frame, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background WAL persistence worker started.");
        var reader = _channel.Reader;
        var batch = new List<ExecutionFrame>(BatchSize);

        while (await reader.WaitToReadAsync(stoppingToken))
        {
            batch.Clear();

            // Drain up to BatchSize frames in one go
            while (batch.Count < BatchSize && reader.TryRead(out var frame))
            {
                batch.Add(frame);
            }

            if (batch.Count == 0) continue;

            try
            {
                if (batch.Count == 1)
                    await _store.SaveFrameAsync(batch[0], stoppingToken);
                else
                    await _store.SaveFramesBatchAsync(batch, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist batch of {Count} execution frames", batch.Count);
            }
        }
    }
}
