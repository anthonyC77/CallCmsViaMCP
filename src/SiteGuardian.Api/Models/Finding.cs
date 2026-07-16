namespace SiteGuardian.Api.Models;

/// <summary>
/// Un problème détecté lors d'un audit. Squelette Phase 0 — enrichi en Phase 1.
/// </summary>
public class Finding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AuditJobId { get; set; }

    public AuditJob? AuditJob { get; set; }

    public FindingSeverity Severity { get; set; } = FindingSeverity.Planifier;

    /// <summary>Ex. « lien-cassé », « faute-orthographe », « meta-description-manquante ».</summary>
    public string Category { get; set; } = string.Empty;

    public string Problem { get; set; } = string.Empty;

    public string? ProposedCorrection { get; set; }

    /// <summary>Nombre de pages concernées (dédoublonnage des textes de thème, cf. §4.1).</summary>
    public int PageCount { get; set; } = 1;
}

/// <summary>Reprend la structure du rapport : Urgent / Important / À planifier.</summary>
public enum FindingSeverity
{
    Urgent,
    Important,
    Planifier
}
