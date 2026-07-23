using System.ComponentModel.DataAnnotations;

namespace SiteGuardian.Api.Models;

/// <summary>
/// Hash SHA-256 du texte de chaque page auditée (audit incrémental, §11 du plan) :
/// d'une semaine sur l'autre, seules les pages modifiées repassent à l'analyse LLM.
/// </summary>
public class PageTextHash
{
    [Key]
    public string Url { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
}
