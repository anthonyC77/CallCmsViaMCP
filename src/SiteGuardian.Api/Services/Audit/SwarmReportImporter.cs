using System.Text.Json;
using System.Text.Json.Serialization;
using SiteGuardian.Api.Models;

namespace SiteGuardian.Api.Services.Audit;

/// <summary>
/// Rapport JSON produit par un agent du swarm (.claude/agents) — schéma commun :
/// { agent, target, summary, score, findings: [{ id, title, severity, category,
///   evidence, impact, fix, effort }] }.
/// </summary>
public class SwarmReport
{
    public string Agent { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? Summary { get; set; }

    /// <summary>Nombre (orchestrateur) ou objet de sous-scores (spécialistes) — on tolère les deux.</summary>
    public JsonElement Score { get; set; }

    public List<SwarmFinding> Findings { get; set; } = new();
}

public class SwarmFinding
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public string Category { get; set; } = string.Empty;

    /// <summary>Chaîne (orchestrateur) ou objet structuré (ex. performance-auditor) — on tolère les deux.</summary>
    public JsonElement Evidence { get; set; }

    public string? Impact { get; set; }
    public string? Fix { get; set; }
    public string Effort { get; set; } = "low";
}

public static class SwarmReportImporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Accepte un rapport unique ou un tableau de rapports.</summary>
    public static List<SwarmReport> Parse(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('['))
            return JsonSerializer.Deserialize<List<SwarmReport>>(json, Options) ?? new();
        var single = JsonSerializer.Deserialize<SwarmReport>(json, Options);
        return single is null ? new() : new List<SwarmReport> { single };
    }

    public static List<Finding> ToFindings(IEnumerable<SwarmReport> reports)
    {
        var findings = new List<Finding>();
        foreach (var report in reports)
        {
            foreach (var f in report.Findings)
            {
                findings.Add(new Finding
                {
                    FindingId = f.Id,
                    Title = f.Title,
                    Severity = ParseSeverity(f.Severity),
                    Category = f.Category,
                    Evidence = ElementToString(f.Evidence),
                    Impact = f.Impact,
                    Fix = f.Fix,
                    Effort = ParseEffort(f.Effort),
                    SourceAgent = report.Agent,
                    Pages = string.IsNullOrEmpty(report.Target) ? new() : new List<string> { report.Target },
                    PageCount = 1,
                });
            }
        }
        return findings;
    }

    public static FindingSeverity ParseSeverity(string value) => value.ToLowerInvariant() switch
    {
        "critical" => FindingSeverity.Critical,
        "high" => FindingSeverity.High,
        "medium" => FindingSeverity.Medium,
        "low" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    public static FindingEffort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "high" => FindingEffort.High,
        "medium" => FindingEffort.Medium,
        _ => FindingEffort.Low,
    };

    private static string? ElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        _ => element.GetRawText(),
    };
}
