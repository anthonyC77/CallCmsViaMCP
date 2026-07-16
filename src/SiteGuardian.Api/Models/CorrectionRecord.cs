namespace SiteGuardian.Api.Models;

/// <summary>
/// Journal d'une correction (qui, quoi, avant/après, quand) pour rollback manuel.
/// Squelette Phase 0 — la boucle agentique arrive en Phase 3 (cf. §4.2).
/// </summary>
public class CorrectionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Instruction en langage naturel saisie par l'utilisateur.</summary>
    public string Instruction { get; set; } = string.Empty;

    public string? Tool { get; set; }

    public string? Target { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }

    public CorrectionStatus Status { get; set; } = CorrectionStatus.Proposed;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>Tokens consommés par la localisation + le plan (garde-fou budgétaire).</summary>
    public int TokensUsed { get; set; }
}

public enum CorrectionStatus
{
    Proposed,
    Applied,
    Rejected,
    Reverted
}
