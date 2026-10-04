using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeterministicProxy.Core.Abstractions;

namespace DeterministicProxy.Core.Canonicalization;

public sealed class SemanticRequestCanonicalizer : IRequestCanonicalizer
{
    public static readonly SemanticRequestCanonicalizer Default = new();

    // Headers that vary per request and cause false non-determinism
    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase)
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
    };

    // JSON fields that often contain dynamic nonces or non-deterministic tokens
    private static readonly HashSet<string> DynamicJsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "nonce",
        "client_timestamp",
        "request_id",
        "uuid",
        "client_session_id"
    };

    public (string NormalizedBodyHash, byte[] CanonicalBytes) CanonicalizeBody(string? contentType, ReadOnlyMemory<byte> rawBody)
    {
        if (rawBody.IsEmpty)
        {
            var emptyHash = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty)).ToLowerInvariant();
            return (emptyHash, Array.Empty<byte>());
        }

        // If JSON, parse, mask dynamic fields, sort keys deterministically
        if (contentType != null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var jsonNode = JsonNode.Parse(rawBody.Span);
                if (jsonNode != null)
                {
                    MaskDynamicFields(jsonNode);
                    var canonicalJson = SerializeCanonicalJson(jsonNode);
                    var canonicalBytes = Encoding.UTF8.GetBytes(canonicalJson);
                    var hash = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
                    return (hash, canonicalBytes);
                }
            }
            catch
            {
                // Fall back to raw hash if JSON parse fails
            }
        }

        var rawHash = Convert.ToHexString(SHA256.HashData(rawBody.Span)).ToLowerInvariant();
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

    private static void MaskDynamicFields(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var prop in obj.ToList())
            {
                if (DynamicJsonKeys.Contains(prop.Key))
                {
                    obj[prop.Key] = "[VIRTUAL_DETERMINISTIC_MASKED]";
                }
                else if (prop.Value != null)
                {
                    MaskDynamicFields(prop.Value);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item != null)
                {
                    MaskDynamicFields(item);
                }
            }
        }
    }

    private static string SerializeCanonicalJson(JsonNode node)
    {
        // Deterministic sorted key JSON serializer
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        WriteSortedJson(node, writer);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSortedJson(JsonNode node, Utf8JsonWriter writer)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var prop in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(prop.Key);
                if (prop.Value == null)
                    writer.WriteNullValue();
                else
                    WriteSortedJson(prop.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (node is JsonArray arr)
        {
            writer.WriteStartArray();
            foreach (var item in arr)
            {
                if (item == null)
                    writer.WriteNullValue();
                else
                    WriteSortedJson(item, writer);
            }
            writer.WriteEndArray();
        }
        else if (node is JsonValue val)
        {
            val.WriteTo(writer);
        }
    }
}
