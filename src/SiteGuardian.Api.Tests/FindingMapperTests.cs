using SiteGuardian.Api.Models;
using SiteGuardian.Api.Services.Audit;

namespace SiteGuardian.Api.Tests;

public class FindingMapperTests
{
    private static ScriptAuditResult HealthyPage(string url) => new()
    {
        Url = url,
        FinalUrl = url,
        StatusCode = 200,
        Https = true,
        RedirectsToHttps = true,
        SslValid = true,
        SslDaysLeft = 90,
        Title = "Un titre de longueur parfaitement raisonnable",
        TitleLength = 44,
        MetaDescription = new string('x', 120),
        MetaDescriptionLength = 120,
        H1Count = 1,
        HasCanonical = true,
        HasLang = true,
        HasViewport = true,
        HasOgTags = true,
        RobotsTxt = true,
        SitemapXml = true,
        ResponseTimeMs = 300,
        PageSizeKb = 500,
        ResourceCount = 40,
    };

    [Fact]
    public void HealthyPage_ProducesNoFindings()
    {
        var findings = FindingMapper.Map(new[] { HealthyPage("https://example.com/") });
        Assert.Empty(findings);
    }

    [Fact]
    public void MissingMetaDescription_MapsToSwarmSchema()
    {
        var page = HealthyPage("https://example.com/");
        page.MetaDescription = "";
        page.MetaDescriptionLength = 0;

        var findings = FindingMapper.Map(new[] { page });

        var f = Assert.Single(findings);
        Assert.Equal("seo-meta-description-missing", f.FindingId);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Equal("seo", f.Category);
        Assert.Equal("web-audit-script", f.SourceAgent);
        Assert.NotNull(f.Fix);
    }

    [Fact]
    public void SameIssueOnManyPages_IsDeduplicatedWithPageCount()
    {
        // Le même problème de thème (ex. lang manquant) sur 3 pages → 1 finding.
        var pages = new[] { "https://example.com/", "https://example.com/a", "https://example.com/b" }
            .Select(u => { var p = HealthyPage(u); p.HasLang = false; return p; })
            .ToArray();

        var findings = FindingMapper.Map(pages);

        var f = Assert.Single(findings);
        Assert.Equal("i18n-lang-missing", f.FindingId);
        Assert.Equal(3, f.PageCount);
        Assert.Equal(3, f.Pages.Count);
    }

    [Fact]
    public void BrokenLink_SharedAcrossPages_GroupsByLinkUrl()
    {
        // Un lien cassé de footer présent sur 2 pages → 1 finding, 2 pages.
        var p1 = HealthyPage("https://example.com/");
        var p2 = HealthyPage("https://example.com/contact");
        p1.BrokenLinks.Add("https://youtube.com/@dead-channel");
        p2.BrokenLinks.Add("https://youtube.com/@dead-channel");

        var findings = FindingMapper.Map(new[] { p1, p2 });

        var f = Assert.Single(findings);
        Assert.Equal("links-broken", f.FindingId);
        Assert.Contains("dead-channel", f.Title);
        Assert.Equal(2, f.PageCount);
        Assert.Equal(FindingSeverity.High, f.Severity);
    }

    [Fact]
    public void UnreachablePage_IsCritical_AndSkipsOtherChecks()
    {
        var page = new ScriptAuditResult
        {
            Url = "https://example.com/down",
            Error = "Requete impossible : timeout",
        };

        var findings = FindingMapper.Map(new[] { page });

        var f = Assert.Single(findings);
        Assert.Equal("page-unreachable", f.FindingId);
        Assert.Equal(FindingSeverity.Critical, f.Severity);
    }

    [Fact]
    public void ManyImagesWithoutAlt_EscalatesToHigh()
    {
        var page = HealthyPage("https://example.com/");
        page.ImagesWithoutAlt = 7;

        var f = Assert.Single(FindingMapper.Map(new[] { page }));
        Assert.Equal("a11y-img-alt", f.FindingId);
        Assert.Equal(FindingSeverity.High, f.Severity);
    }

    [Fact]
    public void ScriptJson_SnakeCase_Deserializes()
    {
        const string json = """
        [{
          "url": "https://example.com",
          "final_url": "https://example.com/",
          "status_code": 200,
          "images_without_alt": 3,
          "ssl_days_left": null,
          "broken_links": ["https://dead.example.com"],
          "security_headers_missing": ["content-security-policy"],
          "has_og_tags": false
        }]
        """;

        var results = ScriptAuditResult.ParseList(json);

        var r = Assert.Single(results);
        Assert.Equal("https://example.com/", r.FinalUrl);
        Assert.Equal(3, r.ImagesWithoutAlt);
        Assert.Null(r.SslDaysLeft);
        Assert.Single(r.BrokenLinks);
        Assert.False(r.HasOgTags);
    }
}
