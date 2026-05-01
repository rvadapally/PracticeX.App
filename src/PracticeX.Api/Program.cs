using Microsoft.AspNetCore.Http.Features;
using PracticeX.Api.Analysis;
using PracticeX.Api.SourceDiscovery;
using PracticeX.Infrastructure;
using PracticeX.Infrastructure.Persistence;
using PracticeX.Infrastructure.Tenancy;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

// Real customer document drops can be >100MB for a folder of leases + scans.
// 256MB ceiling is comfortable for current expectations; revisit if we ever
// see a single bundle over that.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 256L * 1024 * 1024;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 256L * 1024 * 1024;
});

// Slice 16.6 hedge: gzip compression for large narrative responses.
// 30KB JSON briefs compress to ~5KB on the wire — important if any
// upstream proxy has a payload-size threshold for OTP-authenticated paths.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.MimeTypes = ["application/json", "application/json; charset=utf-8", "text/plain", "text/markdown"];
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("CommandCenter", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:5173", "https://localhost:5173"];
        var patterns = builder.Configuration.GetSection("Cors:AllowedOriginPatterns").Get<string[]>()
            ?? Array.Empty<string>();

        // Cloudflare Pages preview URLs follow https://<branch>.<project>.pages.dev,
        // so an exact origin list won't cover them. The pattern matcher accepts
        // simple "*" wildcards in any subdomain position.
        policy.SetIsOriginAllowed(origin =>
        {
            if (origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;
            foreach (var pattern in patterns)
            {
                var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", "[^.]+") + "$";
                if (System.Text.RegularExpressions.Regex.IsMatch(origin, rx)) return true;
            }
            return false;
        }).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});

var app = builder.Build();

// Apply all SQL migration scripts at startup. Every script is idempotent so
// this is safe on every restart. Uses the raw Npgsql connection rather than
// EF Core migrations so no dotnet-ef toolchain is needed at the host.
try
{
    await PracticeX.Infrastructure.Persistence.StartupMigrationRunner.RunAsync(
        scriptAssembly: System.Reflection.Assembly.GetExecutingAssembly(),
        configuration: app.Configuration,
        logger: app.Logger,
        cancellationToken: default);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "StartupMigrationRunner failed — continuing startup. Run apply_all_migrations.sql manually if the API returns 500s.");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "PracticeX Command Center API";
        options.Theme = ScalarTheme.BluePlanet;
    });
}

app.UseResponseCompression();
app.UseCors("CommandCenter");
app.UseHttpsRedirection();

// Friendly root - point devs at the docs instead of returning 404.
app.MapGet("/", () => Results.Content(
    """
    <!DOCTYPE html>
    <html><head><title>PracticeX Command Center API</title>
    <style>body{font-family:system-ui,sans-serif;max-width:640px;margin:4rem auto;padding:0 1.5rem;color:#111}
    code{background:#f4f4f4;padding:.1em .3em;border-radius:3px}
    a{color:#2563eb}</style></head>
    <body>
    <h1>PracticeX Command Center API</h1>
    <p>The API is running. This host serves JSON endpoints; the UI lives on a separate origin.</p>
    <ul>
      <li><a href="/scalar/v1">Interactive API docs (Scalar)</a></li>
      <li><a href="/openapi/v1.json">Raw OpenAPI document</a></li>
      <li><a href="/api/system/info">/api/system/info</a> - quick health probe</li>
      <li><a href="/api/sources/connectors">/api/sources/connectors</a> - registered source connectors</li>
    </ul>
    <p>Frontend: <a href="http://localhost:5173/">http://localhost:5173/</a></p>
    </body></html>
    """, "text/html"));

app.MapGet("/api/system/info", () => Results.Ok(new
{
    product = "PracticeX Command Center",
    posture = "enterprise_data_first",
    database_identifier_policy = "snake_case_unquoted",
    connectors = new[] { "local_folder", "outlook_mailbox" }
}))
.WithName("GetSystemInfo");

app.MapSourceDiscoveryEndpoints();
app.MapAnalysisEndpoints();
app.MapLlmExtractionEndpoints();
app.MapOcrReprocessEndpoints();
app.MapPortfolioBriefEndpoints();

// Demo seed: creates the default tenant + user the demo current-user resolver
// expects. In production this is replaced by tenant onboarding flows.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue("Seeding:DemoTenant", true))
{
    using var scope = app.Services.CreateScope();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<PracticeXDbContext>();
        await DemoCurrentUserContext.EnsureSeededAsync(db, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Skipped demo seed (database unavailable).");
    }
}

app.Run();
