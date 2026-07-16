using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SiteGuardian.Api.Models;

namespace SiteGuardian.Api.Services.Pdf;

/// <summary>
/// Génère le rapport PDF d'un audit, structure calquée sur RapportSite.pdf :
/// sections Urgent / Important / À planifier, chaque finding présenté en
/// « C'est quoi le problème / Pourquoi c'est important / Comment corriger ».
/// </summary>
public static class AuditPdfGenerator
{
    static AuditPdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private sealed record Bucket(string Label, string Color, List<Finding> Findings);

    public static byte[] Generate(AuditJob job)
    {
        var buckets = BuildBuckets(job.Findings);
        var host = Uri.TryCreate(job.TargetUrl, UriKind.Absolute, out var uri) ? uri.Host : job.TargetUrl;
        var date = (job.CompletedAt ?? DateTimeOffset.UtcNow).ToString("dd/MM/yyyy");

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.8f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor(Colors.Grey.Darken4));

                page.Header().Column(col =>
                {
                    col.Item().Text($"Rapport d'audit — {host}")
                        .FontSize(18).Bold().FontColor(Colors.Blue.Darken3);
                    col.Item().PaddingTop(2).Text($"Généré le {date} · {job.PagesAudited} page(s) auditée(s) · SiteGuardian")
                        .FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(8).LineHorizontal(0.75f).LineColor(Colors.Grey.Lighten1);
                });

                page.Content().PaddingTop(12).Column(col =>
                {
                    col.Spacing(14);
                    col.Item().Element(c => ComposeSummary(c, buckets));

                    foreach (var bucket in buckets.Where(b => b.Findings.Count > 0))
                        col.Item().Element(c => ComposeSection(c, bucket));

                    if (job.Findings.Count == 0)
                        col.Item().Text("Aucun problème détecté. 🎉").FontSize(12);
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                    t.Span("SiteGuardian — page ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static List<Bucket> BuildBuckets(IEnumerable<Finding> findings)
    {
        var list = findings.ToList();
        return new List<Bucket>
        {
            new("Urgent", Colors.Red.Darken1,
                list.Where(f => f.Severity is FindingSeverity.Critical or FindingSeverity.High)
                    .OrderBy(f => f.Severity).ThenByDescending(f => f.PageCount).ToList()),
            new("Important", Colors.Orange.Darken2,
                list.Where(f => f.Severity is FindingSeverity.Medium)
                    .OrderByDescending(f => f.PageCount).ToList()),
            new("À planifier", Colors.Blue.Darken1,
                list.Where(f => f.Severity is FindingSeverity.Low or FindingSeverity.Info)
                    .OrderBy(f => f.Severity).ThenByDescending(f => f.PageCount).ToList()),
        };
    }

    private static void ComposeSummary(IContainer container, List<Bucket> buckets)
    {
        container.Row(row =>
        {
            row.Spacing(10);
            foreach (var bucket in buckets)
            {
                row.RelativeItem().Background(Colors.Grey.Lighten4).CornerRadius(4)
                    .Padding(10).Column(col =>
                    {
                        col.Item().Text(bucket.Findings.Count.ToString())
                            .FontSize(22).Bold().FontColor(bucket.Color);
                        col.Item().Text(bucket.Label).FontSize(9).FontColor(Colors.Grey.Darken2);
                    });
            }
        });
    }

    private static void ComposeSection(IContainer container, Bucket bucket)
    {
        container.Column(col =>
        {
            col.Spacing(8);
            col.Item().Text($"{bucket.Label} ({bucket.Findings.Count})")
                .FontSize(14).Bold().FontColor(bucket.Color);

            foreach (var finding in bucket.Findings)
                col.Item().Element(c => ComposeFinding(c, finding, bucket.Color));
        });
    }

    private static void ComposeFinding(IContainer container, Finding finding, string color)
    {
        container
            .Border(0.5f).BorderColor(Colors.Grey.Lighten2).CornerRadius(4)
            .Padding(10)
            .Column(col =>
            {
                col.Spacing(4);

                col.Item().Row(row =>
                {
                    row.RelativeItem().Text(finding.Title).FontSize(10.5f).Bold();
                    row.ConstantItem(110).AlignRight().Text(
                        $"{finding.Category} · {finding.PageCount} page(s)")
                        .FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                });

                Field(col, "C'est quoi le problème ?",
                    finding.Evidence is null ? finding.Title : $"{finding.Title} — {finding.Evidence}");
                if (!string.IsNullOrEmpty(finding.Impact))
                    Field(col, "Pourquoi c'est important ?", finding.Impact);
                if (!string.IsNullOrEmpty(finding.Fix))
                    Field(col, "Comment corriger ?", finding.Fix);

                if (finding.Pages.Count > 1)
                {
                    var shown = finding.Pages.Take(5).ToList();
                    var suffix = finding.Pages.Count > shown.Count
                        ? $" … (+{finding.Pages.Count - shown.Count} autres)"
                        : string.Empty;
                    col.Item().Text($"Pages : {string.Join(", ", shown)}{suffix}")
                        .FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                }
            });
    }

    private static void Field(ColumnDescriptor col, string label, string value)
    {
        col.Item().Text(t =>
        {
            t.Span(label + " ").FontSize(8.5f).SemiBold().FontColor(Colors.Grey.Darken2);
            t.Span(value).FontSize(9);
        });
    }
}
