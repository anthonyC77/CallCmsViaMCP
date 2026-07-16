using SiteGuardian.Api.Models;
using SiteGuardian.Api.Services.Audit;

namespace SiteGuardian.Api.Tests;

public class SwarmReportImporterTests
{
    // Rapport conforme au schéma de .claude/agents/README.md (evidence = objet).
    private const string SpecialistReport = """
    {
      "agent": "performance-auditor",
      "target": "https://example.com",
      "summary": "Mobile LCP is 5.1s, dominated by an unoptimized hero image.",
      "score": { "performance_mobile": 38, "performance_desktop": 71 },
      "findings": [
        {
          "id": "perf-lcp-hero-image",
          "title": "Hero image is 1.8 MB JPEG, no width/height, no AVIF/WebP",
          "severity": "critical",
          "category": "images",
          "evidence": { "metric": "LCP", "value": "5100ms" },
          "impact": "Single largest contributor to mobile LCP failing CWV threshold.",
          "fix": "Serve AVIF/WebP via <picture>.",
          "effort": "low"
        }
      ]
    }
    """;

    [Fact]
    public void SingleReport_WithObjectEvidence_IsImported()
    {
        var reports = SwarmReportImporter.Parse(SpecialistReport);
        var findings = SwarmReportImporter.ToFindings(reports);

        var f = Assert.Single(findings);
        Assert.Equal("perf-lcp-hero-image", f.FindingId);
        Assert.Equal(FindingSeverity.Critical, f.Severity);
        Assert.Equal(FindingEffort.Low, f.Effort);
        Assert.Equal("performance-auditor", f.SourceAgent);
        Assert.Contains("5100ms", f.Evidence);
        Assert.Equal(new List<string> { "https://example.com" }, f.Pages);
    }

    [Fact]
    public void ArrayOfReports_IsImported()
    {
        var json = $"[{SpecialistReport}, {SpecialistReport.Replace("performance-auditor", "seo-auditor")}]";

        var findings = SwarmReportImporter.ToFindings(SwarmReportImporter.Parse(json));

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.SourceAgent == "seo-auditor");
    }

    [Theory]
    [InlineData("critical", FindingSeverity.Critical)]
    [InlineData("HIGH", FindingSeverity.High)]
    [InlineData("medium", FindingSeverity.Medium)]
    [InlineData("low", FindingSeverity.Low)]
    [InlineData("info", FindingSeverity.Info)]
    [InlineData("n-importe-quoi", FindingSeverity.Info)]
    public void Severity_IsParsedLeniently(string input, FindingSeverity expected)
    {
        Assert.Equal(expected, SwarmReportImporter.ParseSeverity(input));
    }
}
