using Microsoft.EntityFrameworkCore;
using SiteGuardian.Api.Data;
using SiteGuardian.Api.Models;

namespace SiteGuardian.Api.Tests;

/// <summary>
/// Tests de fumée Phase 0 : vérifient que le squelette compile, que la référence
/// vers l'Api fonctionne et que le DbContext persiste les entités de base.
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
            Category = "faute-orthographe",
            Problem = "acccompagnement",
            ProposedCorrection = "accompagnement",
            Severity = FindingSeverity.Important,
            PageCount = 43
        });

        context.AuditJobs.Add(job);
        context.SaveChanges();

        var reloaded = context.AuditJobs.Include(a => a.Findings).Single();
        Assert.Single(reloaded.Findings);
        Assert.Equal(43, reloaded.Findings[0].PageCount);
    }

    [Fact]
    public void CorrectionRecord_DefaultStatus_IsProposed()
    {
        var record = new CorrectionRecord { Instruction = "corrige accompagnement partout" };
        Assert.Equal(CorrectionStatus.Proposed, record.Status);
    }
}
