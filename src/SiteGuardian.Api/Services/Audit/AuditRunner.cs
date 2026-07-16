using Microsoft.AspNetCore.SignalR;
using SiteGuardian.Api.Data;
using SiteGuardian.Api.Hubs;
using SiteGuardian.Api.Models;

namespace SiteGuardian.Api.Services.Audit;

/// <summary>
/// Orchestre un audit : sitemap → script Python → mapping findings → persistance,
/// avec progression SignalR. Aucun LLM ici (stratégie coûts §11 : déterministe = 0 €).
/// </summary>
public class AuditRunner
{
    private readonly SiteGuardianDbContext _db;
    private readonly SitemapFetcher _sitemap;
    private readonly PythonAuditService _python;
    private readonly IHubContext<ProgressHub> _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<AuditRunner> _logger;

    public AuditRunner(
        SiteGuardianDbContext db, SitemapFetcher sitemap, PythonAuditService python,
        IHubContext<ProgressHub> hub, IConfiguration config, ILogger<AuditRunner> logger)
    {
        _db = db;
        _sitemap = sitemap;
        _python = python;
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
            var results = await _python.RunAsync(urls, ct);

            var findings = FindingMapper.Map(results);
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
}
