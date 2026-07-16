namespace SiteGuardian.Api.Models;

/// <summary>
/// Un audit lancé sur le site cible. Squelette Phase 0 — enrichi en Phase 1.
/// </summary>
public class AuditJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string TargetUrl { get; set; } = string.Empty;

    public AuditStatus Status { get; set; } = AuditStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Coût estimé de l'audit en EUR (garde-fou budgétaire, cf. §11 du plan).</summary>
    public decimal EstimatedCostEur { get; set; }

    /// <summary>Origine : « script » (web_audit_agent.py) ou « import » (rapport du swarm).</summary>
    public string Source { get; set; } = "script";

    public int PagesAudited { get; set; }

    /// <summary>Message d'erreur si Status == Failed.</summary>
    public string? Error { get; set; }

    public List<Finding> Findings { get; set; } = new();
}

public enum AuditStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
