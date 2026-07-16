using Microsoft.EntityFrameworkCore;
using SiteGuardian.Api.Data;
using SiteGuardian.Api.Hubs;
using SiteGuardian.Api.Models;
using SiteGuardian.Api.Services.Audit;

// --- Point d'entrée CLI (audit sans serveur web, cf. §11 du plan) -----------
// Utilisé par le cron GitHub Actions : `dotnet run -- audit <url>`.
if (args.Length > 0 && args[0].Equals("audit", StringComparison.OrdinalIgnoreCase))
{
    return await SiteGuardian.Api.Cli.AuditCommand.RunAsync(args.Skip(1).ToArray());
}

var builder = WebApplication.CreateBuilder(args);

const string FrontendCorsPolicy = "FrontendCors";

// --- Services ----------------------------------------------------------------
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

builder.Services.AddDbContext<SiteGuardianDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("SiteGuardian")
                      ?? "Data Source=siteguardian.db"));

builder.Services.AddHttpClient<SitemapFetcher>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SiteGuardian/0.1");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton(sp => PythonAuditService.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<IHostEnvironment>().ContentRootPath,
    sp.GetRequiredService<ILogger<PythonAuditService>>()));
builder.Services.AddScoped<AuditRunner>();

// CORS restreint au front local + GitHub Pages (démo lecture seule), cf. §12.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
    options.AddPolicy(FrontendCorsPolicy, policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

var app = builder.Build();

// --- Base de données : création du schéma au démarrage (migrations en Phase 2+)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SiteGuardianDbContext>();
    db.Database.EnsureCreated();
}

// --- Pipeline ----------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors(FrontendCorsPolicy);

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "SiteGuardian.Api",
    phase = "1 — audit"
}))
.WithName("Health");

// Lance un audit (job asynchrone, progression via SignalR /hubs/progress).
app.MapPost("/api/audit", async (
    AuditRequest request, SiteGuardianDbContext db, IServiceScopeFactory scopeFactory) =>
{
    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
        || (uri.Scheme != "http" && uri.Scheme != "https"))
    {
        return Results.BadRequest(new { error = "URL absolue http(s) requise." });
    }

    var job = new AuditJob { TargetUrl = uri.ToString() };
    db.AuditJobs.Add(job);
    await db.SaveChangesAsync();

    _ = Task.Run(async () =>
    {
        using var scope = scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<AuditRunner>();
        await runner.RunAsync(job.Id);
    });

    return Results.Accepted($"/api/audit/{job.Id}", new { id = job.Id });
})
.WithName("StartAudit");

// Statut + findings d'un audit.
app.MapGet("/api/audit/{id:guid}", async (Guid id, SiteGuardianDbContext db) =>
{
    var job = await db.AuditJobs.Include(j => j.Findings)
        .FirstOrDefaultAsync(j => j.Id == id);
    return job is null ? Results.NotFound() : Results.Ok(job);
})
.WithName("GetAudit");

// Télécharge le rapport PDF d'un audit terminé.
app.MapGet("/api/audit/{id:guid}/pdf", async (Guid id, SiteGuardianDbContext db) =>
{
    var job = await db.AuditJobs.Include(j => j.Findings)
        .FirstOrDefaultAsync(j => j.Id == id);
    if (job is null) return Results.NotFound();
    if (job.Status != AuditStatus.Completed)
        return Results.Conflict(new { error = $"Audit non terminé (statut : {job.Status})." });

    var host = Uri.TryCreate(job.TargetUrl, UriKind.Absolute, out var uri) ? uri.Host : "site";
    var bytes = SiteGuardian.Api.Services.Pdf.AuditPdfGenerator.Generate(job);
    return Results.File(bytes, "application/pdf",
        $"audit-{host}-{(job.CompletedAt ?? DateTimeOffset.UtcNow):yyyy-MM-dd}.pdf");
})
.WithName("GetAuditPdf");

// Importe un rapport JSON produit par le swarm d'agents (.claude/agents).
app.MapPost("/api/audit/import", async (HttpRequest request, SiteGuardianDbContext db) =>
{
    using var reader = new StreamReader(request.Body);
    var json = await reader.ReadToEndAsync();

    List<SwarmReport> reports;
    try
    {
        reports = SwarmReportImporter.Parse(json);
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = $"JSON invalide : {ex.Message}" });
    }

    if (reports.Count == 0)
        return Results.BadRequest(new { error = "Aucun rapport dans le corps de la requête." });

    var job = new AuditJob
    {
        TargetUrl = reports[0].Target,
        Source = "import",
        Status = AuditStatus.Completed,
        CompletedAt = DateTimeOffset.UtcNow,
        Findings = SwarmReportImporter.ToFindings(reports),
        PagesAudited = reports.Select(r => r.Target).Distinct().Count(),
    };
    db.AuditJobs.Add(job);
    await db.SaveChangesAsync();

    return Results.Created($"/api/audit/{job.Id}", new { id = job.Id, findings = job.Findings.Count });
})
.WithName("ImportSwarmReport");

app.MapHub<ProgressHub>("/hubs/progress");

app.Run();
return 0;

/// <summary>Corps de POST /api/audit.</summary>
public record AuditRequest(string Url);
