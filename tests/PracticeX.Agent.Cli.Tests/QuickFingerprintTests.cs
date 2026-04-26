using System.Text;
using PracticeX.Agent.Cli.Inventory;

namespace PracticeX.Agent.Cli.Tests;

public class QuickFingerprintTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compute_IsDeterministic_ForIdenticalInputs()
    {
        var first = "12345678"u8;
        var last = "abcdefgh"u8;

        var a = QuickFingerprint.Compute("contract.pdf", 4096, SampleTime, first, last);
        var b = QuickFingerprint.Compute("contract.pdf", 4096, SampleTime, first, last);

        Assert.Equal(a, b);
        Assert.Equal(40, a.Length); // SHA-1 hex == 40 chars
    }

    [Fact]
    public void Compute_DiffersWhenSizeChanges()
    {
        var first = "12345678"u8;
        var last = "abcdefgh"u8;

        var a = QuickFingerprint.Compute("contract.pdf", 4096, SampleTime, first, last);
        var b = QuickFingerprint.Compute("contract.pdf", 8192, SampleTime, first, last);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DiffersWhenContentEdgeBytesChange()
    {
        var first = "12345678"u8;
        var lastA = "abcdefgh"u8;
        var lastB = "abcdefgZ"u8;

        var a = QuickFingerprint.Compute("contract.pdf", 4096, SampleTime, first, lastA);
        var b = QuickFingerprint.Compute("contract.pdf", 4096, SampleTime, first, lastB);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeFromFile_RoundTrips_AgainstManualCompute()
    {
        var path = Path.Combine(Path.GetTempPath(), "fp-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = Encoding.ASCII.GetBytes("12345678middle-content-abcdefgh");
        File.WriteAllBytes(path, bytes);
        try
        {
            var info = new FileInfo(path);

            var first = bytes.AsSpan(0, 8);
            var last = bytes.AsSpan(bytes.Length - 8, 8);
            var manual = QuickFingerprint.Compute(
                info.Name,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                first,
                last);

            var fromFile = QuickFingerprint.ComputeFromFile(info);

            Assert.Equal(manual, fromFile);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
