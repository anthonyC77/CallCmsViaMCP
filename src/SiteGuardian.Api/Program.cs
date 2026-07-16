using Microsoft.EntityFrameworkCore;
using SiteGuardian.Api.Data;
using SiteGuardian.Api.Hubs;

// --- Point d'entrée CLI (audit sans serveur web, cf. §11 du plan) -----------
// Prévu pour le cron GitHub Actions en Phase 1 : `dotnet run -- audit`.
// En Phase 0, il s'agit d'un stub qui confirme juste que le point d'entrée existe.
if (args.Length > 0 && args[0].Equals("audit", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("[SiteGuardian] Mode CLI audit — non implémenté (Phase 1).");
    return;
}

var builder = WebApplication.CreateBuilder(args);

const string FrontendCorsPolicy = "FrontendCors";

// --- Services ----------------------------------------------------------------
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

builder.Services.AddDbContext<SiteGuardianDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("SiteGuardian")
                      ?? "Data Source=siteguardian.db"));

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

// --- Base de données : création du schéma au démarrage (migrations en Phase 1)
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

// Endpoint de santé — vérifie que `dotnet run` répond (critère Phase 0).
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "SiteGuardian.Api",
    phase = "0 — squelette"
}))
.WithName("Health");

app.MapHub<ProgressHub>("/hubs/progress");

app.Run();
