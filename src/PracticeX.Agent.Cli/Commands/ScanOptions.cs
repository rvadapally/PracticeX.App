namespace PracticeX.Agent.Cli.Commands;

/// <summary>
/// Parsed CLI flags for `practicex-agent scan`. Manual parsing keeps the binary
/// dependency-free for v1; Phase 2 may swap in System.CommandLine when more
/// verbs land.
/// </summary>
public sealed record ScanOptions(
    string Root,
    Guid ConnectionId,
    Uri ApiBaseUrl,
    string? Token,
    IReadOnlySet<string> AutoSelectBands,
    bool DryRun,
    bool Insecure,
    string? Notes
)
{
    public static ScanOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Missing command. Usage: practicex-agent scan --root <path> --connection-id <guid> [...]");
        }

        if (!string.Equals(args[0], "scan", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown command '{args[0]}'. Only 'scan' is supported in Phase 1.");
        }

        string? root = null;
        Guid? connectionId = null;
        var apiBaseUrl = new Uri("https://localhost:7100");
        var token = Environment.GetEnvironmentVariable("PRACTICEX_TOKEN");
        var autoSelect = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "strong", "likely" };
        var dryRun = false;
        var insecure = false;
        string? notes = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--root":
                    root = RequireValue(args, ref i, arg);
                    break;
                case "--connection-id":
                    connectionId = Guid.Parse(RequireValue(args, ref i, arg));
                    break;
                case "--api":
                    apiBaseUrl = new Uri(RequireValue(args, ref i, arg));
                    break;
                case "--token":
                    token = RequireValue(args, ref i, arg);
                    break;
                case "--auto-select":
                    autoSelect = new HashSet<string>(
                        RequireValue(args, ref i, arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        StringComparer.OrdinalIgnoreCase);
                    break;
                case "--notes":
                    notes = RequireValue(args, ref i, arg);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--insecure":
                    insecure = true;
                    break;
                case "--help":
                case "-h":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"Unknown flag: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("--root is required.");
        }
        if (connectionId is null)
        {
            throw new ArgumentException("--connection-id is required.");
        }

        return new ScanOptions(root!, connectionId.Value, apiBaseUrl, token, autoSelect, dryRun, insecure, notes);
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{flag} requires a value.");
        }
        return args[++i];
    }
}

public sealed class HelpRequestedException : Exception;
