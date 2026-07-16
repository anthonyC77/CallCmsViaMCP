using Microsoft.EntityFrameworkCore;
using SiteGuardian.Api.Data;
using SiteGuardian.Api.Models;

namespace SiteGuardian.Api.Tests;

/// <summary>
/// Tests de fumée : le DbContext persiste les entités avec le schéma swarm.
/// </summary>
public class SmokeTests
{
    private static SiteGuardianDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<SiteGuardianDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        var context = new SiteGuardianDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public void AuditJob_WithFindings_IsPersisted()
    {
        using var context = NewInMemoryContext();

        var job = new AuditJob { TargetUrl = "https://example.com" };
        job.Findings.Add(new Finding
        {
            FindingId = "content-typo",
            Title = "Faute d'orthographe dans le bandeau",
            Category = "content",
            Severity = FindingSeverity.High,
            Fix = "Corriger le texte du bandeau",
            SourceAgent = "web-audit-script",
            Pages = new List<string> { "https://example.com/", "https://example.com/blog" },
            PageCount = 2,
        });

        context.AuditJobs.Add(job);
        context.SaveChanges();

        var reloaded = context.AuditJobs.Include(a => a.Findings).Single();
        Assert.Single(reloaded.Findings);
        Assert.Equal(2, reloaded.Findings[0].PageCount);
        Assert.Equal(2, reloaded.Findings[0].Pages.Count);
        Assert.Equal(FindingSeverity.High, reloaded.Findings[0].Severity);
    }

    [Fact]
    public void CorrectionRecord_DefaultStatus_IsProposed()
    {
        var record = new CorrectionRecord { Instruction = "corrige accompagnement partout" };
        Assert.Equal(CorrectionStatus.Proposed, record.Status);
    }
}
