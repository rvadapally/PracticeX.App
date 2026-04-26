using PracticeX.Agent.Cli.Commands;

namespace PracticeX.Agent.Cli.Tests;

public class ScanOptionsTests
{
    private const string ConnId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Parse_RequiresScanVerb()
    {
        Assert.Throws<ArgumentException>(() => ScanOptions.Parse(["foo"]));
    }

    [Fact]
    public void Parse_RequiresRoot()
    {
        Assert.Throws<ArgumentException>(() => ScanOptions.Parse(["scan", "--connection-id", ConnId]));
    }

    [Fact]
    public void Parse_RequiresConnectionId()
    {
        Assert.Throws<ArgumentException>(() => ScanOptions.Parse(["scan", "--root", "C:\\Test"]));
    }

    [Fact]
    public void Parse_DefaultsAreSensible()
    {
        var opts = ScanOptions.Parse(["scan", "--root", "C:\\Test", "--connection-id", ConnId]);

        Assert.Equal("C:\\Test", opts.Root);
        Assert.Equal(Guid.Parse(ConnId), opts.ConnectionId);
        Assert.Equal(new Uri("https://localhost:7100"), opts.ApiBaseUrl);
        Assert.False(opts.DryRun);
        Assert.False(opts.Insecure);
        Assert.Contains("strong", opts.AutoSelectBands);
        Assert.Contains("likely", opts.AutoSelectBands);
        Assert.DoesNotContain("possible", opts.AutoSelectBands);
    }

    [Fact]
    public void Parse_HonoursDryRunAndInsecure()
    {
        var opts = ScanOptions.Parse([
            "scan", "--root", "C:\\Test", "--connection-id", ConnId,
            "--dry-run", "--insecure"
        ]);

        Assert.True(opts.DryRun);
        Assert.True(opts.Insecure);
    }

    [Fact]
    public void Parse_AcceptsCustomBandsAndApi()
    {
        var opts = ScanOptions.Parse([
            "scan", "--root", "C:\\Test", "--connection-id", ConnId,
            "--api", "https://example.com",
            "--auto-select", "strong,likely,possible",
            "--notes", "manual run"
        ]);

        Assert.Equal(new Uri("https://example.com"), opts.ApiBaseUrl);
        Assert.Equal(3, opts.AutoSelectBands.Count);
        Assert.Contains("possible", opts.AutoSelectBands);
        Assert.Equal("manual run", opts.Notes);
    }

    [Fact]
    public void Parse_RejectsUnknownFlag()
    {
        Assert.Throws<ArgumentException>(() => ScanOptions.Parse([
            "scan", "--root", "C:\\Test", "--connection-id", ConnId,
            "--mystery-flag"
        ]));
    }

    [Fact]
    public void Parse_HelpFlag_Throws()
    {
        Assert.Throws<HelpRequestedException>(() => ScanOptions.Parse([
            "scan", "--root", "C:\\Test", "--connection-id", ConnId,
            "--help"
        ]));
    }
}
