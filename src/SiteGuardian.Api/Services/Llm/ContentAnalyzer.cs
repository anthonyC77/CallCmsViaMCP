using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiteGuardian.Api.Models;

namespace SiteGuardian.Api.Services.Llm;

/// <summary>
/// Contrôles de contenu par LLM (Agent Surveillance, §4.1 du plan) : valide les
/// candidats du pré-filtre Hunspell par lot de pages, avec garde-fous budgétaires
/// (§11). Sortie structurée { page, extrait, probleme, correction_proposee, severite }.
/// </summary>
public class ContentAnalyzer
{
    private const string SystemPrompt = """
        Tu es le relecteur d'un site web professionnel francophone.
        On te fournit, page par page, des mots suspects détectés par un correcteur
        orthographique, chacun avec son contexte.

        Ta mission : identifier les VRAIES fautes (orthographe, typo, mot anglais
        résiduel dans un texte français). Ignore les noms propres, marques, termes
        techniques, néologismes assumés et mots corrects.

        Réponds UNIQUEMENT avec un tableau JSON (aucun texte autour) :
        [{"page": "<url>", "extrait": "<contexte>", "probleme": "<description courte>",
          "correction_proposee": "<texte corrigé>", "severite": "haute|moyenne|basse"}]
        Tableau vide [] si aucune vraie faute.
        """;

    private readonly ILlmProvider _llm;
    private readonly SpellingPrefilter _prefilter;
    private readonly ContentAnalyzerOptions _options;
    private readonly ILogger<ContentAnalyzer> _logger;

    public ContentAnalyzer(
        ILlmProvider llm, SpellingPrefilter prefilter,
        ContentAnalyzerOptions options, ILogger<ContentAnalyzer> logger)
    {
        _llm = llm;
        _prefilter = prefilter;
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled => _llm.IsEnabled;

    public static string ComputeTextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Analyse les pages fournies (url → texte). Les pages dont le hash figure dans
    /// <paramref name="alreadyAnalyzedHashes"/> sont sautées (audit incrémental, §11).
    /// </summary>
    public async Task<ContentAnalysisResult> AnalyzeAsync(
        IReadOnlyList<(string Url, string Text)> pages,
        IReadOnlySet<string> alreadyAnalyzedHashes,
        CancellationToken ct = default)
    {
        var result = new ContentAnalysisResult();
        if (!_llm.IsEnabled)
        {
            _logger.LogInformation("Analyse LLM sautée : aucune clé API configurée.");
            return result;
        }

        // 1. Incrémental : ne garder que les pages nouvelles/modifiées avec des candidats.
        var toAnalyze = new List<(string Url, string Hash, List<SpellingCandidate> Candidates)>();
        foreach (var (url, text) in pages)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var hash = ComputeTextHash(text);
            result.PageHashes[url] = hash;
            if (alreadyAnalyzedHashes.Contains(hash))
            {
                result.PagesSkipped++;
                continue;
            }

            var candidates = _prefilter.FindCandidates(text);
            if (candidates.Count > 0)
                toAnalyze.Add((url, hash, candidates));
        }

        // Dédoublonnage inter-pages : un même mot de thème (bandeau…) sur N pages
        // n'est envoyé qu'une fois au LLM ; les pages restent tracées pour le finding.
        var wordPages = toAnalyze
            .SelectMany(p => p.Candidates.Select(c => (p.Url, Candidate: c)))
            .GroupBy(x => x.Candidate.Word, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Url).Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase);

        // 2. Batching (§5 : BatchPages) + garde-fous budget (§11).
        foreach (var batch in toAnalyze.Chunk(Math.Max(1, _options.BatchPages)))
        {
            if (result.EstimatedCostEur >= _options.MaxCostEur)
            {
                _logger.LogWarning(
                    "Plafond budget audit atteint ({Cost:F2} €) : analyse incomplète.",
                    result.EstimatedCostEur);
                result.Truncated = true;
                break;
            }

            ct.ThrowIfCancellationRequested();
            var userMessage = BuildBatchMessage(batch);

            LlmResponse response;
            try
            {
                response = await _llm.CompleteAsync(
                    new LlmRequest(_options.Model, SystemPrompt, userMessage), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Échec d'un lot d'analyse LLM — lot ignoré.");
                result.Truncated = true;
                continue;
            }

            result.InputTokens += response.Usage.InputTokens;
            result.OutputTokens += response.Usage.OutputTokens;
            result.EstimatedCostEur =
                (result.InputTokens * _options.InputPricePerMTokEur
                 + result.OutputTokens * _options.OutputPricePerMTokEur) / 1_000_000m;

            foreach (var item in ParseFindings(response.Text))
            {
                var pagesForWord = FindPagesFor(item, wordPages) ?? new List<string> { item.Page };
                result.Findings.Add(new Finding
                {
                    FindingId = "content-typo",
                    Title = item.Probleme,
                    Severity = ParseSeverity(item.Severite),
                    Category = "content",
                    Evidence = item.Extrait,
                    Impact = "Faute visible par les visiteurs — nuit à la crédibilité du site.",
                    Fix = string.IsNullOrEmpty(item.CorrectionProposee)
                        ? null : $"Remplacer par : « {item.CorrectionProposee} »",
                    SourceAgent = "llm-surveillance",
                    Pages = pagesForWord,
                    PageCount = pagesForWord.Count,
                });
            }
        }

        if (result.Truncated)
        {
            result.Findings.Add(new Finding
            {
                FindingId = "meta-analyse-incomplete",
                Title = "Analyse de contenu LLM incomplète",
                Severity = FindingSeverity.Info,
                Category = "meta",
                Evidence = $"Coût estimé : {result.EstimatedCostEur:F2} € (plafond {_options.MaxCostEur:F2} €)",
                Fix = "Relancer un audit, ou augmenter Budget:MaxCoutEstimeParAuditEUR.",
                SourceAgent = "llm-surveillance",
            });
        }

        return result;
    }

    private static string BuildBatchMessage(
        IEnumerable<(string Url, string Hash, List<SpellingCandidate> Candidates)> batch)
    {
        var sb = new StringBuilder();
        foreach (var page in batch)
        {
            sb.AppendLine($"PAGE: {page.Url}");
            foreach (var candidate in page.Candidates)
                sb.AppendLine($"- mot suspect « {candidate.Word} » — contexte : {candidate.Context}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Parse la réponse JSON du modèle (tolère les clôtures ```json).</summary>
    public static List<LlmFindingItem> ParseFindings(string responseText)
    {
        var text = responseText.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return new List<LlmFindingItem>();

        try
        {
            return JsonSerializer.Deserialize<List<LlmFindingItem>>(
                text[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<LlmFindingItem>();
        }
        catch (JsonException)
        {
            return new List<LlmFindingItem>();
        }
    }

    private static FindingSeverity ParseSeverity(string? value) => value?.ToLowerInvariant() switch
    {
        "haute" or "high" or "critical" => FindingSeverity.High,
        "moyenne" or "medium" => FindingSeverity.Medium,
        _ => FindingSeverity.Low,
    };

    private static List<string>? FindPagesFor(
        LlmFindingItem item, Dictionary<string, List<string>> wordPages)
    {
        // Retrouve le mot fautif dans l'extrait pour rattacher toutes les pages concernées.
        foreach (var (word, pages) in wordPages)
        {
            if (item.Extrait?.Contains(word, StringComparison.OrdinalIgnoreCase) == true)
                return pages;
        }
        return null;
    }
}

public record ContentAnalyzerOptions
{
    public string Model { get; init; } = "claude-sonnet-5";
    public int BatchPages { get; init; } = 5;
    public decimal MaxCostEur { get; init; } = 0.50m;
    public decimal InputPricePerMTokEur { get; init; } = 2.8m;
    public decimal OutputPricePerMTokEur { get; init; } = 14m;
}

public class LlmFindingItem
{
    public string Page { get; set; } = string.Empty;
    public string? Extrait { get; set; }
    public string Probleme { get; set; } = string.Empty;

    [JsonPropertyName("correction_proposee")]
    public string? CorrectionProposee { get; set; }

    public string? Severite { get; set; }
}

public class ContentAnalysisResult
{
    public List<Finding> Findings { get; } = new();
    public Dictionary<string, string> PageHashes { get; } = new();
    public int PagesSkipped { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal EstimatedCostEur { get; set; }
    public bool Truncated { get; set; }
}
