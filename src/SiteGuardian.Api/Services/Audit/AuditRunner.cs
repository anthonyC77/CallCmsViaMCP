using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SiteGuardian.Api.Data;
using SiteGuardian.Api.Hubs;
using SiteGuardian.Api.Models;
using SiteGuardian.Api.Services.Llm;

namespace SiteGuardian.Api.Services.Audit;

/// <summary>
/// Orchestre un audit : sitemap → script Python (déterministe, 0 €) → mapping
/// findings → analyse de contenu LLM (incrémentale, plafonnée §11) → persistance,
/// avec progression SignalR.
/// </summary>
public class AuditRunner
{
    private readonly SiteGuardianDbContext _db;
    private readonly SitemapFetcher _sitemap;
    private readonly PythonAuditService _python;
    private readonly ContentAnalyzer _contentAnalyzer;
    private readonly IHubContext<ProgressHub> _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<AuditRunner> _logger;

    public AuditRunner(
        SiteGuardianDbContext db, SitemapFetcher sitemap, PythonAuditService python,
        ContentAnalyzer contentAnalyzer, IHubContext<ProgressHub> hub,
        IConfiguration config, ILogger<AuditRunner> logger)
    {
        _db = db;
        _sitemap = sitemap;
        _python = python;
        _contentAnalyzer = contentAnalyzer;
        _hub = hub;
        _config = config;
        _logger = logger;
    }

    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.AuditJobs.FindAsync(new object[] { jobId }, ct)
            ?? throw new InvalidOperationException($"AuditJob {jobId} introuvable.");

        try
        {
            job.Status = AuditStatus.Running;
            await _db.SaveChangesAsync(ct);
            await NotifyAsync(jobId, "running", "Récupération du sitemap…", ct);

            var root = new Uri(job.TargetUrl);
            var maxPages = _config.GetValue("Audit:MaxPages", 60);
            var urls = await _sitemap.GetPageUrlsAsync(root, maxPages, ct);

            await NotifyAsync(jobId, "auditing", $"Audit de {urls.Count} page(s) via web_audit_agent.py…", ct);
            var includeText = _contentAnalyzer.IsEnabled;
            var results = await _python.RunAsync(urls, includeText, ct);

            var findings = FindingMapper.Map(results);

            if (includeText)
            {
                await NotifyAsync(jobId, "analyzing", "Analyse de contenu (LLM, incrémentale)…", ct);
                var analysis = await RunContentAnalysisAsync(_db, _contentAnalyzer, results, ct);
                findings.AddRange(analysis.Findings);
                job.EstimatedCostEur = analysis.EstimatedCostEur;
            }

            foreach (var finding in findings)
                finding.AuditJobId = job.Id;

            job.Findings = findings;
            job.PagesAudited = results.Count;
            job.Status = AuditStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await NotifyAsync(jobId, "completed",
                $"Audit terminé : {findings.Count} finding(s) sur {results.Count} page(s).", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Audit {JobId} échoué", jobId);
            job.Status = AuditStatus.Failed;
            job.Error = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            await NotifyAsync(jobId, "failed", ex.Message, CancellationToken.None);
        }
    }

    private Task NotifyAsync(Guid jobId, string status, string message, CancellationToken ct) =>
        _hub.Clients.All.SendAsync("auditProgress", new { jobId, status, message }, ct);

    /// <summary>
    /// Analyse de contenu LLM incrémentale (§4.1/§11 du plan) : partagée entre le
    /// serveur web (<see cref="RunAsync"/>) et le mode CLI (<see cref="Cli.AuditCommand"/>),
    /// tous deux disposant d'une <see cref="SiteGuardianDbContext"/> pour l'historique
    /// des hashes de page.
    /// </summary>
    public static async Task<ContentAnalysisResult> RunContentAnalysisAsync(
        SiteGuardianDbContext db, ContentAnalyzer analyzer,
        IReadOnlyList<ScriptAuditResult> results, CancellationToken ct = default)
    {
        var pages = results
            .Where(r => !string.IsNullOrWhiteSpace(r.TextContent))
            .Select(r => (Url: string.IsNullOrEmpty(r.FinalUrl) ? r.Url : r.FinalUrl, Text: r.TextContent))
            .ToList();

        var knownHashes = (await db.PageTextHashes.Select(h => h.Sha256).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var analysis = await analyzer.AnalyzeAsync(pages, knownHashes, ct);

        // Mémorise les hashes pour l'audit incrémental suivant.
        foreach (var (url, hash) in analysis.PageHashes)
        {
            var existing = await db.PageTextHashes.FindAsync(new object[] { url }, ct);
            if (existing is null)
                db.PageTextHashes.Add(new PageTextHash { Url = url, Sha256 = hash });
            else if (!string.Equals(existing.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                existing.Sha256 = hash;
                existing.AnalyzedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);

        return analysis;
    }
}
