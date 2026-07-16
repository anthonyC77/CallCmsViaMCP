using SiteGuardian.Api.Models;

namespace SiteGuardian.Api.Services.Audit;

/// <summary>
/// Transforme les résultats page-par-page de web_audit_agent.py en findings
/// au schéma du swarm, avec dédoublonnage : un même problème présent sur N pages
/// devient UN finding listant les N pages (ex. bandeau de thème visible partout).
/// </summary>
public static class FindingMapper
{
    private const string Source = "web-audit-script";

    public static List<Finding> Map(IReadOnlyList<ScriptAuditResult> results)
    {
        // Clé de regroupement : (FindingId, Title) — les pages s'accumulent.
        var acc = new Dictionary<(string Id, string Title), Finding>();

        foreach (var r in results)
        {
            var page = string.IsNullOrEmpty(r.FinalUrl) ? r.Url : r.FinalUrl;

            if (!string.IsNullOrEmpty(r.Error))
            {
                Add(acc, page, "page-unreachable", $"Page injoignable : {r.Error}",
                    FindingSeverity.Critical, "links", evidence: page,
                    impact: "La page est inaccessible aux visiteurs et aux moteurs.",
                    fix: "Vérifier l'URL, la redirection ou l'hébergement.");
                continue;
            }

            if (r.StatusCode >= 400)
                Add(acc, page, "links-http-error", $"La page répond HTTP {r.StatusCode}",
                    FindingSeverity.Critical, "links", evidence: page,
                    impact: "Page en erreur pour les visiteurs et les moteurs.",
                    fix: "Corriger ou rediriger cette URL.");

            MapSecurity(acc, r, page);
            MapPerformance(acc, r, page);
            MapSeo(acc, r, page);
            MapAccessibility(acc, r, page);

            foreach (var link in r.BrokenLinks)
                Add(acc, page, "links-broken", $"Lien cassé : {link}",
                    FindingSeverity.High, "links", evidence: link,
                    impact: "Erreur au clic pour les visiteurs, signal négatif SEO.",
                    fix: "Corriger ou supprimer ce lien.");
        }

        var findings = acc.Values.ToList();
        foreach (var f in findings)
        {
            f.Pages = f.Pages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            f.PageCount = Math.Max(1, f.Pages.Count);
        }
        return findings.OrderBy(f => f.Severity).ThenByDescending(f => f.PageCount).ToList();
    }

    private static void MapSecurity(Dictionary<(string, string), Finding> acc, ScriptAuditResult r, string page)
    {
        if (!r.Https)
            Add(acc, page, "sec-no-https", "Site servi en HTTP (non chiffré)",
                FindingSeverity.Critical, "security", evidence: page,
                impact: "Données en clair ; avertissement navigateur ; pénalité SEO.",
                fix: "Activer HTTPS et rediriger tout le trafic HTTP.", FindingEffort.Medium);
        else
        {
            if (!r.RedirectsToHttps)
                Add(acc, page, "sec-no-https-redirect", "HTTP ne redirige pas vers HTTPS",
                    FindingSeverity.High, "security", evidence: page,
                    impact: "Les visiteurs arrivant en HTTP restent sur une version non chiffrée.",
                    fix: "Ajouter une redirection 301 HTTP → HTTPS.", FindingEffort.Low);
            if (!r.SslValid)
                Add(acc, page, "sec-ssl-invalid", "Certificat SSL invalide ou injoignable",
                    FindingSeverity.Critical, "security", evidence: page,
                    impact: "Avertissement de sécurité bloquant pour les visiteurs.",
                    fix: "Renouveler / reconfigurer le certificat TLS.", FindingEffort.Medium);
            else if (r.SslDaysLeft is { } days && days < 30)
                Add(acc, page, "sec-ssl-expiring", $"Certificat SSL expire dans {days} jour(s)",
                    days < 15 ? FindingSeverity.High : FindingSeverity.Medium, "security",
                    evidence: page, impact: "Le site deviendra inaccessible à expiration.",
                    fix: "Renouveler le certificat (ou vérifier l'auto-renouvellement).");
        }

        if (r.SecurityHeadersMissing.Count > 0)
            Add(acc, page, "sec-headers-missing", "En-têtes de sécurité HTTP manquants",
                FindingSeverity.Medium, "security",
                evidence: string.Join(", ", r.SecurityHeadersMissing),
                impact: "Protections navigateur absentes (clickjacking, XSS, sniffing).",
                fix: "Configurer les en-têtes manquants au niveau de l'hébergeur/du thème.",
                FindingEffort.Medium);
    }

    private static void MapPerformance(Dictionary<(string, string), Finding> acc, ScriptAuditResult r, string page)
    {
        if (r.ResponseTimeMs > 1500)
            Add(acc, page, "perf-slow-response", $"Réponse lente ({r.ResponseTimeMs} ms)",
                FindingSeverity.High, "performance", evidence: $"{page} — {r.ResponseTimeMs} ms",
                impact: "Attente perceptible ; Core Web Vitals dégradés.",
                fix: "Optimiser images/scripts, activer cache et compression.", FindingEffort.Medium);
        if (r.PageSizeKb > 2048)
            Add(acc, page, "perf-page-heavy", $"Page très lourde ({r.PageSizeKb} Ko)",
                FindingSeverity.High, "performance", evidence: $"{page} — {r.PageSizeKb} Ko",
                impact: "Chargement long, surtout sur mobile.",
                fix: "Compresser les images (WebP/AVIF), différer les scripts.", FindingEffort.Medium);
        if (r.ResourceCount > 100)
            Add(acc, page, "perf-many-resources", $"Beaucoup de ressources ({r.ResourceCount})",
                FindingSeverity.Medium, "performance", evidence: page,
                impact: "Multiplication des requêtes réseau.",
                fix: "Regrouper/limiter scripts, styles et images.", FindingEffort.Medium);
    }

    private static void MapSeo(Dictionary<(string, string), Finding> acc, ScriptAuditResult r, string page)
    {
        if (string.IsNullOrEmpty(r.Title))
            Add(acc, page, "seo-title-missing", "Balise <title> manquante",
                FindingSeverity.High, "seo", evidence: page,
                impact: "Le titre est le premier signal SEO d'une page.",
                fix: "Rédiger un title unique et descriptif (30–65 caractères).");
        else if (r.TitleLength < 10)
            Add(acc, page, "seo-title-short", "Balise <title> trop courte",
                FindingSeverity.Medium, "seo", evidence: $"{page} — « {r.Title} »",
                impact: "Titre peu informatif dans les résultats de recherche.",
                fix: "Étoffer le title (30–65 caractères).");
        else if (r.TitleLength > 65)
            Add(acc, page, "seo-title-long", $"Balise <title> trop longue ({r.TitleLength} car.)",
                FindingSeverity.Low, "seo", evidence: page,
                impact: "Titre tronqué dans les résultats de recherche.",
                fix: "Raccourcir sous ~65 caractères.");

        if (string.IsNullOrEmpty(r.MetaDescription))
            Add(acc, page, "seo-meta-description-missing", "Meta description manquante",
                FindingSeverity.High, "seo", evidence: page,
                impact: "Google génère un extrait arbitraire → taux de clic dégradé.",
                fix: "Rédiger une meta description engageante (50–160 caractères).");
        else if (r.MetaDescriptionLength < 50)
            Add(acc, page, "seo-meta-description-short", "Meta description trop courte",
                FindingSeverity.Low, "seo", evidence: page,
                fix: "Étoffer la description (50–160 caractères).");
        else if (r.MetaDescriptionLength > 165)
            Add(acc, page, "seo-meta-description-long", "Meta description trop longue",
                FindingSeverity.Low, "seo", evidence: page,
                fix: "Raccourcir sous ~160 caractères.");

        if (r.H1Count == 0)
            Add(acc, page, "seo-h1-missing", "Aucune balise <h1>",
                FindingSeverity.High, "seo", evidence: page,
                impact: "Structure de page illisible pour les moteurs.",
                fix: "Ajouter un h1 unique reprenant le sujet de la page.");
        else if (r.H1Count > 1)
            Add(acc, page, "seo-h1-multiple", $"{r.H1Count} balises <h1> (1 seule recommandée)",
                FindingSeverity.Medium, "seo", evidence: page,
                fix: "Ne garder qu'un h1, rétrograder les autres en h2.");

        if (!r.HasCanonical)
            Add(acc, page, "seo-canonical-missing", "Canonical manquant",
                FindingSeverity.Medium, "seo", evidence: page,
                impact: "Risque de contenu dupliqué aux yeux des moteurs.",
                fix: "Ajouter <link rel=\"canonical\"> sur chaque page.");
        if (!r.HasOgTags)
            Add(acc, page, "seo-og-missing", "Aucune balise Open Graph",
                FindingSeverity.Medium, "seo", evidence: page,
                impact: "Partages sociaux sans titre/image maîtrisés.",
                fix: "Ajouter og:title, og:description, og:image.");
        if (!r.HasLang)
            Add(acc, page, "i18n-lang-missing", "Attribut lang manquant sur <html>",
                FindingSeverity.Medium, "i18n", evidence: page,
                impact: "Lecteurs d'écran et moteurs ignorent la langue du contenu.",
                fix: "Ajouter lang=\"fr\" sur la balise <html> du thème.");
        if (!r.HasViewport)
            Add(acc, page, "mobile-viewport-missing", "Meta viewport manquant (responsive)",
                FindingSeverity.High, "mobile", evidence: page,
                impact: "Rendu mobile cassé ; pénalité mobile-first.",
                fix: "Ajouter <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">.");

        if (!r.RobotsTxt)
            Add(acc, page, "seo-robots-missing", "Pas de robots.txt",
                FindingSeverity.Low, "seo", evidence: page,
                fix: "Publier un robots.txt pointant vers le sitemap.");
        if (!r.SitemapXml)
            Add(acc, page, "seo-sitemap-missing", "Pas de sitemap.xml",
                FindingSeverity.Medium, "seo", evidence: page,
                impact: "Indexation plus lente et incomplète.",
                fix: "Publier un sitemap.xml et le déclarer dans Search Console.");
    }

    private static void MapAccessibility(Dictionary<(string, string), Finding> acc, ScriptAuditResult r, string page)
    {
        if (r.ImagesWithoutAlt > 0)
            Add(acc, page, "a11y-img-alt", "Images sans attribut alt",
                r.ImagesWithoutAlt > 5 ? FindingSeverity.High : FindingSeverity.Medium,
                "accessibility", evidence: $"{page} — {r.ImagesWithoutAlt} image(s)",
                impact: "Contenu invisible pour les lecteurs d'écran ; SEO images perdu.",
                fix: "Ajouter un alt descriptif à chaque image porteuse de sens.");
        if (r.InputsWithoutLabel > 0)
            Add(acc, page, "a11y-input-label", "Champs de formulaire sans label",
                FindingSeverity.Medium, "accessibility",
                evidence: $"{page} — {r.InputsWithoutLabel} champ(s)",
                fix: "Associer un <label> (ou aria-label) à chaque champ.");
        if (r.ButtonsWithoutName > 0)
            Add(acc, page, "a11y-button-name", "Boutons sans nom accessible",
                FindingSeverity.Medium, "accessibility",
                evidence: $"{page} — {r.ButtonsWithoutName} bouton(s)",
                fix: "Donner un texte ou un aria-label à chaque bouton.");
        if (r.LinksWithoutText > 0)
            Add(acc, page, "a11y-link-text", "Liens sans texte",
                FindingSeverity.Medium, "accessibility",
                evidence: $"{page} — {r.LinksWithoutText} lien(s)",
                fix: "Ajouter un texte ou un aria-label aux liens (icônes incluses).");
        if (r.HeadingSkips > 0)
            Add(acc, page, "a11y-heading-skip", "Sauts de niveaux de titres",
                FindingSeverity.Low, "accessibility",
                evidence: $"{page} — {r.HeadingSkips} saut(s)",
                fix: "Respecter la hiérarchie h1 → h2 → h3 sans sauter de niveau.");
    }

    private static void Add(
        Dictionary<(string, string), Finding> acc, string page,
        string findingId, string title, FindingSeverity severity, string category,
        string? evidence = null, string? impact = null, string? fix = null,
        FindingEffort effort = FindingEffort.Low)
    {
        var key = (findingId, title);
        if (!acc.TryGetValue(key, out var finding))
        {
            finding = new Finding
            {
                FindingId = findingId,
                Title = title,
                Severity = severity,
                Category = category,
                Evidence = evidence,
                Impact = impact,
                Fix = fix,
                Effort = effort,
                SourceAgent = Source,
            };
            acc[key] = finding;
        }
        finding.Pages.Add(page);

        // L'évidence reste courte : on n'accumule pas une ligne par page.
        if (evidence is not null && finding.Evidence is not null
            && !finding.Evidence.Contains(evidence, StringComparison.OrdinalIgnoreCase)
            && finding.Evidence.Length + evidence.Length < 400)
        {
            finding.Evidence += " ; " + evidence;
        }
    }
}
