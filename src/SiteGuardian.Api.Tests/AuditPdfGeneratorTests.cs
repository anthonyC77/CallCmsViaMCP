using SiteGuardian.Api.Models;
using SiteGuardian.Api.Services.Pdf;

namespace SiteGuardian.Api.Tests;

public class AuditPdfGeneratorTests
{
    private static AuditJob SampleJob() => new()
    {
        TargetUrl = "https://example.com",
        Status = AuditStatus.Completed,
        CompletedAt = DateTimeOffset.UtcNow,
        PagesAudited = 43,
        Findings = new List<Finding>
        {
            new()
            {
                FindingId = "content-typo",
                Title = "Faute d'orthographe dans le bandeau d'annonce",
                Severity = FindingSeverity.High,
                Category = "content",
                Evidence = "« acccompagnement »",
                Impact = "Visible sur toutes les pages, nuit à la crédibilité.",
                Fix = "Corriger le texte du bandeau dans le thème.",
                Pages = Enumerable.Range(1, 43).Select(i => $"https://example.com/p{i}").ToList(),
                PageCount = 43,
            },
            new()
            {
                FindingId = "seo-meta-description-missing",
                Title = "Meta description manquante",
                Severity = FindingSeverity.Medium,
                Category = "seo",
                Fix = "Rédiger une meta description (50–160 caractères).",
                Pages = new List<string> { "https://example.com/" },
                PageCount = 1,
            },
            new()
            {
                FindingId = "seo-robots-missing",
                Title = "Pas de robots.txt",
                Severity = FindingSeverity.Low,
                Category = "seo",
                Pages = new List<string> { "https://example.com/" },
                PageCount = 1,
            },
        },
    };

    [Fact]
    public void Generate_ProducesValidPdf()
    {
        var bytes = AuditPdfGenerator.Generate(SampleJob());

        Assert.True(bytes.Length > 1000, "PDF anormalement petit");
        // Signature PDF : %PDF
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, bytes.Take(4).ToArray());
    }

    [Fact]
    public void Generate_EmptyAudit_StillProducesPdf()
    {
        var job = new AuditJob
        {
            TargetUrl = "https://example.com",
            Status = AuditStatus.Completed,
            PagesAudited = 5,
        };

        var bytes = AuditPdfGenerator.Generate(job);
        Assert.True(bytes.Length > 500);
    }
}
