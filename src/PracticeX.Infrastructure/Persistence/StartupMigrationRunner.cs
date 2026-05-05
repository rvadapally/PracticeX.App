using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PracticeX.Infrastructure.Persistence;

/// <summary>
/// Applies the embedded SQL migration scripts at API startup using the raw
/// Npgsql connection (not EF Core migrations). Scripts are embedded in the
/// PracticeX.Api assembly under the logical name prefix "migrations.*".
///
/// ADR 0005 requires migrations to be idempotent and to never abort startup.
/// This runner is the production "self-bootstrap" for managed-Postgres
/// targets (Render / Fly / Azure / Supabase) where there is no human
/// operator to run psql before the first request. It is intentionally a
/// no-op when the database connection cannot be opened, so local dev
/// without Postgres still boots.
/// </summary>
public sealed class StartupMigrationRunner
{
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
                cmd.CommandTimeout = 180;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                logger.LogInformation("StartupMigrationRunner: applied {Name}", name);
            }
            catch (Exception ex)
            {
                // Idempotent guards in each script make re-runs safe; a partial
                // re-apply may still surface harmless errors. Logging at
                // warning level keeps the API responsive even when one
                // script trips, which matches the on-call posture for the
                // demo window.
                logger.LogWarning(ex, "StartupMigrationRunner: error applying {Name} — {Message}", name, ex.Message);
            }
        }

        logger.LogInformation("StartupMigrationRunner: finished.");
    }
}
