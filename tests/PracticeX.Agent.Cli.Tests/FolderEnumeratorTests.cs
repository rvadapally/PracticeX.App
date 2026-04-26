using PracticeX.Agent.Cli.Inventory;

namespace PracticeX.Agent.Cli.Tests;

public class FolderEnumeratorTests : IDisposable
{
    private readonly string _root;

    public FolderEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "practicex-agent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Enumerate_PicksUpContractFiles_PreservesRelativePaths()
    {
        WriteFile("Payers/BCBS/2024 Amendment 3.pdf", "%PDF-1.4 fake but non-zero");
        WriteFile("Vendors/Olympus/Service Renewal.docx", "PK fake docx bytes");
        WriteFile("Leases/Northside/Suite 310 Lease.pdf", "%PDF-1.4 lease");

        var items = FolderEnumerator.Enumerate(_root).ToList();

        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.RelativePath == "Payers/BCBS/2024 Amendment 3.pdf");
        Assert.Contains(items, i => i.RelativePath == "Vendors/Olympus/Service Renewal.docx");
        Assert.Contains(items, i => i.RelativePath == "Leases/Northside/Suite 310 Lease.pdf");
    }

    [Fact]
    public void Enumerate_SkipsZeroByteFiles()
    {
        WriteFile("real.pdf", "%PDF-1.4 something");
        WriteFile("placeholder.pdf", "");

        var items = FolderEnumerator.Enumerate(_root).ToList();

        Assert.Single(items);
        Assert.Equal("real.pdf", items[0].RelativePath);
    }

    [Fact]
    public void Enumerate_SkipsKnownNoiseExtensions()
    {
        WriteFile("contract.pdf", "%PDF-1.4 contract");
        WriteFile("debug.log", "log content");
        WriteFile("scratch.tmp", "temp content");
        WriteFile("readme.bak", "backup");

        var items = FolderEnumerator.Enumerate(_root).ToList();

        Assert.Single(items);
        Assert.Equal("contract.pdf", items[0].RelativePath);
    }

    [Fact]
    public void Enumerate_SkipsKnownNoiseDirectories()
    {
        WriteFile("contract.pdf", "%PDF-1.4 keep");
        WriteFile("node_modules/foo/index.pdf", "%PDF-1.4 noise");
        WriteFile(".git/HEAD.pdf", "%PDF-1.4 noise");
        WriteFile("__pycache__/cached.pdf", "%PDF-1.4 noise");

        var items = FolderEnumerator.Enumerate(_root).ToList();

        Assert.Single(items);
        Assert.Equal("contract.pdf", items[0].RelativePath);
    }

    [Fact]
    public void Enumerate_GuessesMimeFromExtension()
    {
        WriteFile("a.pdf", "%PDF-1.4");
        WriteFile("b.docx", "PK");
        WriteFile("c.txt", "hello");
        WriteFile("d.unknown", "????");

        var items = FolderEnumerator.Enumerate(_root).ToDictionary(i => i.RelativePath);

        Assert.Equal("application/pdf", items["a.pdf"].MimeType);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", items["b.docx"].MimeType);
        Assert.Equal("text/plain", items["c.txt"].MimeType);
        Assert.Equal("application/octet-stream", items["d.unknown"].MimeType);
    }

    [Fact]
    public void Enumerate_MissingRoot_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => FolderEnumerator.Enumerate(Path.Combine(_root, "nope")).ToList());
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
