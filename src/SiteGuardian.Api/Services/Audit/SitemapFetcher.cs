using System.Xml.Linq;

namespace SiteGuardian.Api.Services.Audit;

/// <summary>
/// Récupère la liste des pages d'un site via sitemap.xml (gère les sitemap index).
/// C'est la seule brique de crawl côté C# : le script Python audite page par page,
/// il faut donc lui fournir la liste d'URLs.
/// </summary>
public class SitemapFetcher
{
    private readonly HttpClient _http;

    public SitemapFetcher(HttpClient http) => _http = http;

    /// <summary>
    /// Retourne jusqu'à <paramref name="maxPages"/> URLs du même hôte que
    /// <paramref name="root"/>. Fallback : la racine seule si pas de sitemap.
    /// </summary>
    public async Task<List<string>> GetPageUrlsAsync(Uri root, int maxPages, CancellationToken ct = default)
    {
        var sitemapUrl = new Uri(root, "/sitemap.xml");
        var urls = new List<string>();
        try
        {
            var xml = await _http.GetStringAsync(sitemapUrl, ct);
            urls = ParseSitemap(xml, root);

            // Sitemap index : suivre les sous-sitemaps (bornés) jusqu'à remplir maxPages.
            if (urls.Count > 0 && urls[0].EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var children = urls.Take(5).ToList();
                urls = new List<string>();
                foreach (var child in children)
                {
                    if (urls.Count >= maxPages) break;
                    try
                    {
                        var childXml = await _http.GetStringAsync(child, ct);
                        urls.AddRange(ParseSitemap(childXml, root));
                    }
                    catch (HttpRequestException) { /* sous-sitemap injoignable : on continue */ }
                }
            }
        }
        catch (HttpRequestException)
        {
            // Pas de sitemap : le script auditera au moins la page d'accueil.
        }

        if (urls.Count == 0)
            urls.Add(root.ToString());

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxPages).ToList();
    }

    /// <summary>Extrait les &lt;loc&gt; d'un sitemap (urlset ou sitemapindex), même hôte uniquement.</summary>
    public static List<string> ParseSitemap(string xml, Uri root)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "loc")
                .Select(e => e.Value.Trim())
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
                            && string.Equals(uri.Host, root.Host, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return new List<string>();
        }
    }
}
