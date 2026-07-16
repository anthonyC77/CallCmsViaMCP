using SiteGuardian.Api.Services.Audit;

namespace SiteGuardian.Api.Tests;

public class SitemapParserTests
{
    private static readonly Uri Root = new("https://example.com");

    [Fact]
    public void Urlset_ExtractsSameHostUrls()
    {
        const string xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>https://example.com/</loc></url>
          <url><loc>https://example.com/blog</loc></url>
          <url><loc>https://other-site.com/spam</loc></url>
        </urlset>
        """;

        var urls = SitemapFetcher.ParseSitemap(xml, Root);

        Assert.Equal(2, urls.Count);
        Assert.DoesNotContain(urls, u => u.Contains("other-site"));
    }

    [Fact]
    public void SitemapIndex_ExtractsChildSitemapUrls()
    {
        const string xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <sitemap><loc>https://example.com/sitemap-pages.xml</loc></sitemap>
          <sitemap><loc>https://example.com/sitemap-blog.xml</loc></sitemap>
        </sitemapindex>
        """;

        var urls = SitemapFetcher.ParseSitemap(xml, Root);

        Assert.Equal(2, urls.Count);
        Assert.All(urls, u => Assert.EndsWith(".xml", u));
    }

    [Fact]
    public void InvalidXml_ReturnsEmpty()
    {
        Assert.Empty(SitemapFetcher.ParseSitemap("<html>pas un sitemap</html", Root));
    }
}
