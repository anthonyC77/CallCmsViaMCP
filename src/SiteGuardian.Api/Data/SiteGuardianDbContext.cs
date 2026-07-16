using Microsoft.EntityFrameworkCore;
using SiteGuardian.Api.Models;

namespace SiteGuardian.Api.Data;

public class SiteGuardianDbContext : DbContext
{
    public SiteGuardianDbContext(DbContextOptions<SiteGuardianDbContext> options)
        : base(options)
    {
    }

    public DbSet<AuditJob> AuditJobs => Set<AuditJob>();

    public DbSet<Finding> Findings => Set<Finding>();

    public DbSet<CorrectionRecord> Corrections => Set<CorrectionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditJob>()
            .HasMany(a => a.Findings)
            .WithOne(f => f.AuditJob)
            .HasForeignKey(f => f.AuditJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
