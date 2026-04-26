using System.Security.Cryptography;
using System.Text;

namespace PracticeX.Agent.Cli.Inventory;

/// <summary>
/// Cheap content fingerprint used as a stable identifier for delta scans
/// (Phase 2). Spec parity with the agent's eventual local_index column.
///
/// sha1(name | size | mtime | first8B | last8B). For files smaller than 16 B
/// the bytes are emitted in full to keep the fingerprint defined.
/// </summary>
public static class QuickFingerprint
{
    public static string Compute(string fileName, long sizeBytes, DateTimeOffset modifiedUtc, ReadOnlySpan<byte> first8, ReadOnlySpan<byte> last8)
    {
        using var sha1 = SHA1.Create();
        Span<byte> buffer = stackalloc byte[16 + 16];

        var headerBuilder = new StringBuilder();
        headerBuilder.Append(fileName);
        headerBuilder.Append('|').Append(sizeBytes);
        headerBuilder.Append('|').Append(modifiedUtc.ToUnixTimeSeconds());
        var headerBytes = Encoding.UTF8.GetBytes(headerBuilder.ToString());

        sha1.TransformBlock(headerBytes, 0, headerBytes.Length, null, 0);

        // .NET 9 SHA1 wants either streaming TransformBlock or Hash. We accumulate
        // header + first + last via TransformBlock and finalize with TransformFinalBlock.
        if (!first8.IsEmpty)
        {
            var copy = first8.ToArray();
            sha1.TransformBlock(copy, 0, copy.Length, null, 0);
        }
        if (!last8.IsEmpty)
        {
            var copy = last8.ToArray();
            sha1.TransformBlock(copy, 0, copy.Length, null, 0);
        }
        sha1.TransformFinalBlock([], 0, 0);

        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    public static string ComputeFromFile(FileInfo file)
    {
        using var stream = file.OpenRead();

        Span<byte> first = stackalloc byte[8];
        Span<byte> last = stackalloc byte[8];

        var firstRead = ReadExactly(stream, first);
        if (file.Length > 8)
        {
            stream.Seek(Math.Max(0, file.Length - 8), SeekOrigin.Begin);
            ReadExactly(stream, last);
        }
        else
        {
            // Tiny files: last 8 == first 8 (covers the spec for sizes <= 8).
            first.CopyTo(last);
        }

        return Compute(
            file.Name,
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            first[..firstRead],
            last);
    }

    private static int ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
