using PracticeX.Agent.Cli.Http;

namespace PracticeX.Agent.Cli.Inventory;

/// <summary>
/// Walks a folder tree and yields manifest items for everything worth scoring.
/// Skips system / hidden / known-noise directories and known-noise extensions
/// before they hit the wire so the cloud manifest scan sees only candidates.
/// </summary>
public static class FolderEnumerator
{
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        ".git",
        ".svn",
        ".hg",
        ".idea",
        ".vs",
        "__pycache__",
        "bin",
        "obj",
        "$RECYCLE.BIN",
        "System Volume Information"
    };

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".log", ".lnk", ".bak", ".swp",
        ".ds_store", ".lock", ".pyc", ".pyo", ".o", ".obj", ".dll", ".exe", ".class"
    };

    public static IEnumerable<ManifestItemDto> Enumerate(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Scan root does not exist: {root}");
        }

        return EnumerateRecursive(rootInfo, rootInfo.FullName);
    }

    private static IEnumerable<ManifestItemDto> EnumerateRecursive(DirectoryInfo dir, string rootFullPath)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (ShouldSkip(entry))
            {
                continue;
            }

            switch (entry)
            {
                case DirectoryInfo subdir:
                    foreach (var item in EnumerateRecursive(subdir, rootFullPath))
                    {
                        yield return item;
                    }
                    break;
                case FileInfo file when ShouldInclude(file):
                    yield return ToManifestItem(file, rootFullPath);
                    break;
            }
        }
    }

    public static bool ShouldSkip(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.Hidden) != 0) return true;
        if ((entry.Attributes & FileAttributes.System) != 0) return true;
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return true;

        if (entry is DirectoryInfo dir && SkipDirNames.Contains(dir.Name))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldInclude(FileInfo file)
    {
        if (file.Length == 0) return false;
        if (SkipExtensions.Contains(file.Extension)) return false;
        return true;
    }

    public static ManifestItemDto ToManifestItem(FileInfo file, string rootFullPath)
    {
        var relativePath = Path.GetRelativePath(rootFullPath, file.FullName).Replace('\\', '/');
        return new ManifestItemDto(
            RelativePath: relativePath,
            Name: file.Name,
            SizeBytes: file.Length,
            LastModifiedUtc: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            MimeType: MimeMap.GuessMimeType(file.Extension)
        );
    }
}

internal static class MimeMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".rtf"] = "application/rtf",
        [".eml"] = "message/rfc822",
        [".msg"] = "application/vnd.ms-outlook",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".xml"] = "application/xml",
        [".json"] = "application/json",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff"
    };

    public static string GuessMimeType(string extension)
        => Map.TryGetValue(extension, out var mime) ? mime : "application/octet-stream";
}
