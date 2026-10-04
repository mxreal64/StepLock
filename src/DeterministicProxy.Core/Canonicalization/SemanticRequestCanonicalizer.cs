using System.Buffers;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using DeterministicProxy.Core.Abstractions;

namespace DeterministicProxy.Core.Canonicalization;

public sealed class SemanticRequestCanonicalizer : IRequestCanonicalizer
{
    public static readonly SemanticRequestCanonicalizer Default = new();

    // Headers that vary per request and cause false non-determinism
    private static readonly FrozenSet<string> IgnoredHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Date",
        "User-Agent",
        "X-Request-Id",
        "X-Correlation-Id",
        "Traceparent",
        "Tracestate",
        "Sec-Ch-Ua",
        "Sec-Ch-Ua-Mobile",
        "Sec-Ch-Ua-Platform",
        "X-Agent-Session-ID",
        "X-Step-Index",
        "X-Branch-Id",
        "X-Execution-Mode",
        "X-Virtual-Time",
        "Host"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // JSON fields that often contain dynamic nonces or non-deterministic tokens
    private static readonly FrozenSet<string> DynamicJsonKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nonce",
        "client_timestamp",
        "request_id",
        "uuid",
        "client_session_id"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly byte[] EmptySha256Bytes = SHA256.HashData(ReadOnlySpan<byte>.Empty);
    private static readonly string EmptySha256Hex = Convert.ToHexStringLower(EmptySha256Bytes);

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_bufferWriter;

    [ThreadStatic]
    private static Utf8JsonWriter? t_jsonWriter;

    public (string NormalizedBodyHash, byte[] CanonicalBytes) CanonicalizeBody(string? contentType, ReadOnlyMemory<byte> rawBody)
    {
        if (rawBody.IsEmpty)
        {
            return (EmptySha256Hex, Array.Empty<byte>());
        }

        // If JSON, parse, mask dynamic fields, sort keys deterministically
        if (contentType != null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                var bufferWriter = t_bufferWriter ??= new ArrayBufferWriter<byte>(1024);
                bufferWriter.Clear();

                var jsonWriter = t_jsonWriter;
                if (jsonWriter == null)
                {
                    jsonWriter = t_jsonWriter = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions { Indented = false });
                }
                else
                {
                    jsonWriter.Reset(bufferWriter);
                }

                WriteCanonicalElement(doc.RootElement, jsonWriter);
                jsonWriter.Flush();

                var writtenSpan = bufferWriter.WrittenSpan;
                var canonicalBytes = writtenSpan.ToArray();

                Span<byte> hashBytes = stackalloc byte[32];
                SHA256.HashData(writtenSpan, hashBytes);
                var hash = Convert.ToHexStringLower(hashBytes);

                return (hash, canonicalBytes);
            }
            catch
            {
                // Fall back to raw hash if JSON parse fails
            }
        }

        Span<byte> rawHashBytes = stackalloc byte[32];
        SHA256.HashData(rawBody.Span, rawHashBytes);
        var rawHash = Convert.ToHexStringLower(rawHashBytes);
        return (rawHash, rawBody.ToArray());
    }

    public Dictionary<string, string> CanonicalizeHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (IgnoredHeaders.Contains(header.Key))
                continue;

            result[header.Key] = string.Join(",", header.Value);
        }
        return new Dictionary<string, string>(result);
    }

    private readonly struct FastProp
    {
        public readonly string Name;
        public readonly JsonElement Value;

        public FastProp(string name, JsonElement value)
        {
            Name = name;
            Value = value;
        }
    }

    private static void WriteCanonicalElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();

                var rented = ArrayPool<FastProp>.Shared.Rent(16);
                int count = 0;
                try
                {
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (count == rented.Length)
                        {
                            var nextRented = ArrayPool<FastProp>.Shared.Rent(rented.Length * 2);
                            rented.AsSpan(0, count).CopyTo(nextRented);
                            ArrayPool<FastProp>.Shared.Return(rented);
                            rented = nextRented;
                        }
                        rented[count++] = new FastProp(prop.Name, prop.Value);
                    }

                    if (count > 0)
                    {
                        var activeSlice = rented.AsSpan(0, count);
                        activeSlice.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

                        for (int i = 0; i < activeSlice.Length; i++)
                        {
                            ref readonly var prop = ref activeSlice[i];
                            writer.WritePropertyName(prop.Name);

                            if (DynamicJsonKeys.Contains(prop.Name))
                            {
                                writer.WriteStringValue("[VIRTUAL_DETERMINISTIC_MASKED]");
                            }
                            else
                            {
                                WriteCanonicalElement(prop.Value, writer);
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<FastProp>.Shared.Return(rented);
                }

                writer.WriteEndObject();
                break;
            }

            case JsonValueKind.Array:
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(item, writer);
                }
                writer.WriteEndArray();
                break;
            }

            default:
            {
                element.WriteTo(writer);
                break;
            }
        }
    }
}
