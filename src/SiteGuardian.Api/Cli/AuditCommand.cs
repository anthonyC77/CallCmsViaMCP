using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SiteGuardian.Api.Data;
using SiteGuardian.Api.Models;
using SiteGuardian.Api.Services.Audit;
using SiteGuardian.Api.Services.Llm;

namespace SiteGuardian.Api.Cli;

/// <summary>
/// Mode CLI : `dotnet run -- audit &lt;url&gt; [--max-pages N] [--pdf chemin.pdf]`.
/// Audit complet sans serveur web — pensé pour le cron GitHub Actions (§11).
/// Contrôles déterministes toujours exécutés ; l'analyse de contenu LLM ne
/// s'active que si une clé Anthropic est configurée (sinon 0 €, cf. §11).
/// Sortie : findings groupés par sévérité sur stdout (+ PDF en option).
/// Code retour 0 = OK, 1 = erreur.
/// </summary>
public static class AuditCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || !Uri.TryCreate(args[0], UriKind.Absolute, out var root))
        {
            Console.Error.WriteLine("Usage : dotnet run -- audit <url> [--max-pages N] [--pdf chemin.pdf]");
            return 1;
        }

        var maxPages = 60;
        string? pdfPath = null;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--max-pages" && int.TryParse(args[i + 1], out var n))
                maxPages = n;
            if (args[i] == "--pdf")
                pdfPath = args[i + 1];
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets(typeof(AuditCommand).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SiteGuardian/0.1");

            var sitemap = new SitemapFetcher(http);
            var python = new PythonAuditService(
                config["Audit:PythonExecutable"] ?? "python",
                PythonAuditService.ResolveScriptPath(
                    config["Audit:ScriptPath"] ?? "web_audit_agent.py", AppContext.BaseDirectory),
                NullLogger<PythonAuditService>.Instance);

            var apiKey = config["Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            ILlmProvider llm = string.IsNullOrWhiteSpace(apiKey)
                ? new DisabledLlmProvider()
                : new AnthropicLlmProvider(apiKey, NullLogger<AnthropicLlmProvider>.Instance);

            var contentAnalyzer = new ContentAnalyzer(
                llm,
                SpellingPrefilter.CreateFrench(AppContext.BaseDirectory),
                new ContentAnalyzerOptions
                {
                    Model = config["Agents:Surveillance:Model"] ?? "claude-sonnet-5",
                    BatchPages = config.GetValue("Agents:Surveillance:BatchPages", 5),
                    MaxCostEur = config.GetValue("Budget:MaxCoutEstimeParAuditEUR", 0.50m),
                    InputPricePerMTokEur = config.GetValue("Budget:PrixEntreeEURParMTokens", 2.8m),
                    OutputPricePerMTokEur = config.GetValue("Budget:PrixSortieEURParMTokens", 14m),
                },
                NullLogger<ContentAnalyzer>.Instance);

            if (!contentAnalyzer.IsEnabled)
                Console.WriteLine("[SiteGuardian] Aucune clé Anthropic configurée : contrôles déterministes uniquement (0 €).");

            var dbOptions = new DbContextOptionsBuilder<SiteGuardianDbContext>()
                .UseSqlite(config.GetConnectionString("SiteGuardian") ?? "Data Source=siteguardian.db")
                .Options;
            using var db = new SiteGuardianDbContext(dbOptions);
            db.Database.EnsureCreated();

            Console.WriteLine($"[SiteGuardian] Sitemap de {root} …");
            var urls = await sitemap.GetPageUrlsAsync(root, maxPages);
            Console.WriteLine($"[SiteGuardian] {urls.Count} page(s) à auditer via web_audit_agent.py …");

            var results = await python.RunAsync(urls, includeText: contentAnalyzer.IsEnabled);
            var findings = FindingMapper.Map(results);

            if (contentAnalyzer.IsEnabled)
            {
                Console.WriteLine("[SiteGuardian] Analyse de contenu (LLM, incrémentale)…");
                var analysis = await AuditRunner.RunContentAnalysisAsync(db, contentAnalyzer, results);
                findings.AddRange(analysis.Findings);
                if (analysis.EstimatedCostEur > 0)
                    Console.WriteLine($"[SiteGuardian] Coût LLM estimé : {analysis.EstimatedCostEur:F3} €" +
                        (analysis.PagesSkipped > 0 ? $" ({analysis.PagesSkipped} page(s) inchangée(s) sautée(s))" : ""));
            }

            Console.WriteLine();
            Console.WriteLine($"=== {findings.Count} finding(s) sur {results.Count} page(s) ===");
            foreach (var group in findings.GroupBy(f => f.Severity).OrderBy(g => g.Key))
            {
                Console.WriteLine($"\n--- {group.Key.ToString().ToUpperInvariant()} ({group.Count()}) ---");
                foreach (var f in group)
                    Console.WriteLine($"  [{f.Category}] {f.Title} — {f.PageCount} page(s)");
            }

            if (pdfPath is not null)
            {
                var job = new AuditJob
                {
                    TargetUrl = root.ToString(),
                    Status = AuditStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    PagesAudited = results.Count,
                    Findings = findings,
                };
                await File.WriteAllBytesAsync(pdfPath, Services.Pdf.AuditPdfGenerator.Generate(job));
                Console.WriteLine($"\n[SiteGuardian] PDF écrit : {Path.GetFullPath(pdfPath)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SiteGuardian] Échec de l'audit : {ex.Message}");
            return 1;
        }
    }
}
