using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using DeterministicProxy.Core.Abstractions;

namespace DeterministicProxy.Core.Cryptography;

public sealed class FrameHasher : IFrameHasher
{
    public static readonly FrameHasher Instance = new();

    public string ComputeFrameHash(
        string? parentStepHash,
        string httpMethod,
        string targetUri,
        string requestBodyHash,
        int stepIndex)
    {
        // Lineage cryptographic chaining (Merkle DAG):
        // Hash(ParentHash + StepIndex + Method + NormalizedTargetUri + NormalizedBodyHash)
        var parent = parentStepHash ?? "ROOT";
        int maxCharLen = parent.Length + 1 + 11 + 1 + httpMethod.Length + 1 + targetUri.Length + 1 + requestBodyHash.Length;

        char[]? rentedChars = null;
        Span<char> charBuffer = maxCharLen <= 1024
            ? stackalloc char[1024]
            : (rentedChars = ArrayPool<char>.Shared.Rent(maxCharLen));

        try
        {
            int charPos = 0;
            parent.AsSpan().CopyTo(charBuffer.Slice(charPos));
            charPos += parent.Length;
            charBuffer[charPos++] = ':';

            if (!stepIndex.TryFormat(charBuffer.Slice(charPos), out int stepWritten))
            {
                var s = stepIndex.ToString();
                s.AsSpan().CopyTo(charBuffer.Slice(charPos));
                stepWritten = s.Length;
            }
            charPos += stepWritten;
            charBuffer[charPos++] = ':';

            for (int i = 0; i < httpMethod.Length; i++)
            {
                charBuffer[charPos++] = char.ToUpperInvariant(httpMethod[i]);
            }
            charBuffer[charPos++] = ':';

            // Normalize URI directly into charBuffer without allocations
            int uriWritten = NormalizeUriToSpan(targetUri.AsSpan(), charBuffer.Slice(charPos));
            charPos += uriWritten;
            charBuffer[charPos++] = ':';

            requestBodyHash.AsSpan().CopyTo(charBuffer.Slice(charPos));
            charPos += requestBodyHash.Length;

            ReadOnlySpan<char> finalChars = charBuffer.Slice(0, charPos);

            int maxByteLen = Encoding.UTF8.GetMaxByteCount(finalChars.Length);
            byte[]? rentedBytes = null;
            Span<byte> byteBuffer = maxByteLen <= 2048
                ? stackalloc byte[2048]
                : (rentedBytes = ArrayPool<byte>.Shared.Rent(maxByteLen));

            try
            {
                int bytesWritten = Encoding.UTF8.GetBytes(finalChars, byteBuffer);
                Span<byte> hashBytes = stackalloc byte[32];
                SHA256.HashData(byteBuffer.Slice(0, bytesWritten), hashBytes);
                return Convert.ToHexStringLower(hashBytes);
            }
            finally
            {
                if (rentedBytes != null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBytes);
                }
            }
        }
        finally
        {
            if (rentedChars != null)
            {
                ArrayPool<char>.Shared.Return(rentedChars);
            }
        }
    }

    private static int NormalizeUriToSpan(ReadOnlySpan<char> uri, Span<char> destination)
    {
        // Strip query string ('?') and fragment ('#')
        int queryIdx = uri.IndexOfAny('?', '#');
        ReadOnlySpan<char> cleanUri = queryIdx >= 0 ? uri.Slice(0, queryIdx) : uri;

        // If absolute URI (contains "://")
        int schemeIdx = cleanUri.IndexOf("://".AsSpan(), StringComparison.Ordinal);
        if (schemeIdx > 0)
        {
            // scheme://authority/path
            ReadOnlySpan<char> scheme = cleanUri.Slice(0, schemeIdx);
            ReadOnlySpan<char> rest = cleanUri.Slice(schemeIdx + 3);

            int destPos = 0;
            for (int i = 0; i < scheme.Length; i++)
            {
                destination[destPos++] = char.ToLowerInvariant(scheme[i]);
            }
            destination[destPos++] = ':';
            destination[destPos++] = '/';
            destination[destPos++] = '/';

            for (int i = 0; i < rest.Length; i++)
            {
                destination[destPos++] = char.ToLowerInvariant(rest[i]);
            }
            return destPos;
        }

        // Relative or custom URI
        for (int i = 0; i < cleanUri.Length; i++)
        {
            destination[i] = char.ToLowerInvariant(cleanUri[i]);
        }
        return cleanUri.Length;
    }
}
