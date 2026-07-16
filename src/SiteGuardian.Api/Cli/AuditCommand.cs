using Microsoft.Extensions.Logging.Abstractions;
using SiteGuardian.Api.Models;
using SiteGuardian.Api.Services.Audit;

namespace SiteGuardian.Api.Cli;

/// <summary>
/// Mode CLI : `dotnet run -- audit &lt;url&gt; [--max-pages N] [--pdf chemin.pdf]`.
/// Audit complet sans serveur web ni base — pensé pour le cron GitHub Actions (§11).
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

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SiteGuardian/0.1");

            var sitemap = new SitemapFetcher(http);
            var python = new PythonAuditService(
                Environment.GetEnvironmentVariable("SITEGUARDIAN_PYTHON") ?? "python",
                PythonAuditService.ResolveScriptPath(
                    Environment.GetEnvironmentVariable("SITEGUARDIAN_SCRIPT") ?? "web_audit_agent.py",
                    AppContext.BaseDirectory),
                NullLogger<PythonAuditService>.Instance);

            Console.WriteLine($"[SiteGuardian] Sitemap de {root} …");
            var urls = await sitemap.GetPageUrlsAsync(root, maxPages);
            Console.WriteLine($"[SiteGuardian] {urls.Count} page(s) à auditer via web_audit_agent.py …");

            var results = await python.RunAsync(urls);
            var findings = FindingMapper.Map(results);

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
