using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PracticeX.Infrastructure.Persistence;

/// <summary>
/// Applies all embedded SQL migration scripts at API startup using the raw
/// Npgsql connection (not EF Core migrations). Scripts are embedded in the
/// PracticeX.Api assembly under the logical name prefix "migrations.*".
///
/// Every script is idempotent (uses IF NOT EXISTS / ADD COLUMN IF NOT EXISTS)
/// so running them on an already-current database is safe.
///
/// This runner is intentionally separate from EF Core migrations to keep
/// deployment simple: no dotnet-ef toolchain needed at the host, and no
/// migration lock contention during startup.
/// </summary>
public sealed class StartupMigrationRunner
{
    /// <summary>
    /// The ordered list of logical resource names that map to the embedded SQL
    /// scripts. Order matters — later scripts depend on tables/columns created
    /// by earlier ones.
    /// </summary>
    private static readonly string[] ScriptOrder =
    [
        "migrations.practicex_initial_enterprise_foundation.sql",
        "migrations.20260425_source_discovery_extensions.sql",
        "migrations.20260426_manifest_phase_extensions.sql",
        "migrations.20260427_doc_intel_layout.sql",
        "migrations.20260427_complexity_profiling.sql",
        "migrations.20260428_extracted_fields.sql",
        "migrations.20260429_extracted_full_text.sql",
        "migrations.20260429_llm_extracted_fields.sql",
        "migrations.20260430_llm_narrative_brief.sql",
        "migrations.20260430_portfolio_brief.sql",
    ];

    public static async Task RunAsync(
        Assembly scriptAssembly,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("PracticeX")
            ?? "Host=localhost;Port=5432;Database=practicex;Username=postgres;Password=postgres";

        await using var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "StartupMigrationRunner: could not open DB connection — skipping migrations.");
            return;
        }

        var resources = scriptAssembly.GetManifestResourceNames();

        foreach (var name in ScriptOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find the embedded resource — logical name or assembly-qualified name.
            var resourceName = resources.FirstOrDefault(r =>
                r.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                r.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                logger.LogWarning("StartupMigrationRunner: embedded resource '{Name}' not found — skipping.", name);
                continue;
            }

            string sql;
            using (var stream = scriptAssembly.GetManifestResourceStream(resourceName)!)
            using (var reader = new StreamReader(stream))
            {
                sql = await reader.ReadToEndAsync(cancellationToken);
            }

            try
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                logger.LogInformation("StartupMigrationRunner: applied {Name}", name);
            }
            catch (Exception ex)
            {
                // Log and continue — a script that was already fully applied
                // may produce harmless errors on a partial re-run. The
                // idempotent guards in each script prevent most of these, but
                // a partial transaction failure on first run can leave things
                // in a valid intermediate state.
                logger.LogWarning(ex, "StartupMigrationRunner: error applying {Name} — {Message}", name, ex.Message);
            }
        }

        logger.LogInformation("StartupMigrationRunner: finished.");
    }
}
