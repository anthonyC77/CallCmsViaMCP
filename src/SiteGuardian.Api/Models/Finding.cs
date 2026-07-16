namespace SiteGuardian.Api.Models;

/// <summary>
/// Un problème détecté lors d'un audit. Le schéma est aligné sur celui du swarm
/// d'agents d'audit (.claude/agents) pour que l'appli puisse ingérer indifféremment
/// les sorties de web_audit_agent.py et les rapports JSON du swarm.
/// </summary>
public class Finding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AuditJobId { get; set; }

    public AuditJob? AuditJob { get; set; }

    /// <summary>Id stable façon swarm, ex. « seo-meta-description-missing ».</summary>
    public string FindingId { get; set; } = string.Empty;

    /// <summary>Ce qui ne va pas, en une phrase.</summary>
    public string Title { get; set; } = string.Empty;

    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;

    /// <summary>Sous-catégorie façon swarm : seo, links, accessibility, security, performance, i18n, mobile…</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>URL / sélecteur / extrait / en-tête prouvant le problème.</summary>
    public string? Evidence { get; set; }

    /// <summary>Pourquoi c'est important.</summary>
    public string? Impact { get; set; }

    /// <summary>Remédiation concrète.</summary>
    public string? Fix { get; set; }

    public FindingEffort Effort { get; set; } = FindingEffort.Low;

    /// <summary>Origine du finding : « web-audit-script », ou le nom d'un agent du swarm.</summary>
    public string SourceAgent { get; set; } = string.Empty;

    /// <summary>Pages concernées (dédoublonnage : un même problème sur N pages = 1 finding).</summary>
    public List<string> Pages { get; set; } = new();

    public int PageCount { get; set; } = 1;
}

/// <summary>Échelle du swarm : critical | high | medium | low | info.</summary>
public enum FindingSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info
}

/// <summary>Effort de remédiation façon swarm : low | medium | high.</summary>
public enum FindingEffort
{
    Low,
    Medium,
    High
}
