using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Engine.WebSocket;

public sealed class WebSocketInterceptor
{
    public static async Task<List<WebSocketCapturedFrame>> ProxyAndRecordAsync(
        System.Net.WebSockets.WebSocket clientWs,
        System.Net.WebSockets.WebSocket upstreamWs,
        CancellationToken ct = default)
    {
        var capturedFrames = new ConcurrentBag<WebSocketCapturedFrame>();
        var stopwatch = Stopwatch.StartNew();
        var sequenceCounter = new SequenceHolder();

        var clientToUpstream = ForwardAsync(
            source: clientWs,
            dest: upstreamWs,
            isClientToServer: true,
            capturedFrames,
            stopwatch,
            sequenceCounter,
            ct);

        var upstreamToClient = ForwardAsync(
            source: upstreamWs,
            dest: clientWs,
            isClientToServer: false,
            capturedFrames,
            stopwatch,
            sequenceCounter,
            ct);

        await Task.WhenAll(clientToUpstream, upstreamToClient);

        return capturedFrames.OrderBy(f => f.Sequence).ToList();
    }

    private static async Task ForwardAsync(
        System.Net.WebSockets.WebSocket source,
        System.Net.WebSockets.WebSocket dest,
        bool isClientToServer,
        ConcurrentBag<WebSocketCapturedFrame> capturedFrames,
        Stopwatch stopwatch,
        SequenceHolder sequenceCounter,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            while (source.State == WebSocketState.Open &&
                   dest.State == WebSocketState.Open &&
                   !ct.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await dest.CloseAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        ct);
                    break;
                }

                var payload = result.MessageType == WebSocketMessageType.Text
                    ? Encoding.UTF8.GetString(buffer, 0, result.Count)
                    : Convert.ToBase64String(buffer, 0, result.Count);

                capturedFrames.Add(new WebSocketCapturedFrame(
                    Sequence: sequenceCounter.Next(),
                    TimestampMs: stopwatch.ElapsedMilliseconds,
                    IsClientToServer: isClientToServer,
                    MessageType: result.MessageType.ToString(),
                    Payload: payload
                ));

                await dest.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class SequenceHolder
    {
        private int _current;
        public int Next() => Interlocked.Increment(ref _current) - 1;
    }
}
