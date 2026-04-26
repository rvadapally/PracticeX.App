using PracticeX.Agent.Cli.Commands;

var stdout = Console.Out;
var stderr = Console.Error;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintHelp(stdout);
    return 0;
}

ScanOptions options;
try
{
    options = ScanOptions.Parse(args);
}
catch (HelpRequestedException)
{
    PrintHelp(stdout);
    return 0;
}
catch (ArgumentException ex)
{
    stderr.WriteLine($"error: {ex.Message}");
    PrintHelp(stderr);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await ScanCommand.RunAsync(options, stdout, stderr, cts.Token);
}
catch (OperationCanceledException)
{
    stderr.WriteLine("cancelled.");
    return 130;
}
catch (Exception ex)
{
    stderr.WriteLine($"unexpected error: {ex.Message}");
    return 2;
}

static void PrintHelp(TextWriter w)
{
    w.WriteLine("PracticeX Facility Discovery Agent — Phase 1 CLI");
    w.WriteLine();
    w.WriteLine("Usage:");
    w.WriteLine("  practicex-agent scan --root <path> --connection-id <guid> [options]");
    w.WriteLine();
    w.WriteLine("Required:");
    w.WriteLine("  --root <path>            Folder to scan recursively.");
    w.WriteLine("  --connection-id <guid>   Existing source_connections.id of type local_folder.");
    w.WriteLine();
    w.WriteLine("Options:");
    w.WriteLine("  --api <url>              API base URL (default https://localhost:7100).");
    w.WriteLine("  --token <bearer>         Bearer token (default reads PRACTICEX_TOKEN env var).");
    w.WriteLine("  --auto-select <bands>    Comma-list of bands to upload (default 'strong,likely').");
    w.WriteLine("                           Valid: strong, likely, possible. 'skipped' is never uploadable.");
    w.WriteLine("  --notes <text>           Free-text notes attached to the ingestion batch.");
    w.WriteLine("  --dry-run                Run manifest scan and print report; skip bundle upload.");
    w.WriteLine("  --insecure               Skip TLS validation (dev / self-signed certs only).");
    w.WriteLine("  -h, --help               Show this help.");
    w.WriteLine();
    w.WriteLine("Exit codes: 0 success, 1 partial (some items failed), 2 transport/config error.");
}
