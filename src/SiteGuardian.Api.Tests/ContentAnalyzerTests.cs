using Microsoft.Extensions.Logging.Abstractions;
using SiteGuardian.Api.Models;
using SiteGuardian.Api.Services.Llm;
using SiteGuardian.Api.Tests.Fakes;

namespace SiteGuardian.Api.Tests;

public class ContentAnalyzerTests
{
    private static ContentAnalyzer CreateAnalyzer(
        FakeLlmProvider provider, ContentAnalyzerOptions? options = null) =>
        new(
            provider,
            SpellingPrefilter.CreateFrench(SpellingPrefilterTests_FindRoot()),
            options ?? new ContentAnalyzerOptions { BatchPages = 5, MaxCostEur = 0.50m },
            NullLogger<ContentAnalyzer>.Instance);

    private static string SpellingPrefilterTests_FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "SiteGuardian.Api");
            if (File.Exists(Path.Combine(candidate, "Dictionaries", "fr", "fr.dic")))
                return candidate;
        }
        throw new InvalidOperationException("Dictionnaire FR introuvable pour les tests.");
    }

    [Fact]
    public async Task DisabledProvider_SkipsAnalysis_ReturnsEmptyResult()
    {
        var analyzer = new ContentAnalyzer(
            new DisabledLlmProvider(),
            SpellingPrefilter.CreateFrench(SpellingPrefilterTests_FindRoot()),
            new ContentAnalyzerOptions(),
            NullLogger<ContentAnalyzer>.Instance);

        Assert.False(analyzer.IsEnabled);

        var result = await analyzer.AnalyzeAsync(
            new List<(string, string)> { ("https://example.com/", "acccompagnement personnalisé") },
            new HashSet<string>());

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task RealTypo_ProducesFindingFromLlmResponse()
    {
        const string llmJson = """
        [{"page": "https://example.com/", "extrait": "acccompagnement",
          "probleme": "Faute d'orthographe", "correction_proposee": "accompagnement",
          "severite": "haute"}]
        """;
        var provider = new FakeLlmProvider(new LlmResponse(llmJson, new LlmUsage(1000, 200)));
        var analyzer = CreateAnalyzer(provider);

        var result = await analyzer.AnalyzeAsync(
            new List<(string, string)>
            {
                ("https://example.com/", "Nous proposons un acccompagnement personnalisé."),
            },
            new HashSet<string>());

        var finding = Assert.Single(result.Findings);
        Assert.Equal("content-typo", finding.FindingId);
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal("llm-surveillance", finding.SourceAgent);
        Assert.Contains("accompagnement", finding.Fix);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task SameTypoAcrossPages_IsGroupedIntoOneFindingWithAllPages()
    {
        const string llmJson = """
        [{"page": "https://example.com/", "extrait": "acccompagnement",
          "probleme": "Faute d'orthographe", "correction_proposee": "accompagnement",
          "severite": "haute"}]
        """;
        var provider = new FakeLlmProvider(new LlmResponse(llmJson, new LlmUsage(500, 100)));
        var analyzer = CreateAnalyzer(provider);

        var pages = new List<(string, string)>
        {
            ("https://example.com/", "Notre acccompagnement est unique."),
            ("https://example.com/a-propos", "Un acccompagnement de qualité."),
            ("https://example.com/contact", "Contactez-nous pour un acccompagnement."),
        };

        var result = await analyzer.AnalyzeAsync(pages, new HashSet<string>());

        var finding = Assert.Single(result.Findings);
        Assert.Equal(3, finding.PageCount);
        Assert.Equal(3, finding.Pages.Count);
    }

    [Fact]
    public async Task AlreadyAnalyzedHash_SkipsPage_NoLlmCall()
    {
        var provider = new FakeLlmProvider();
        var analyzer = CreateAnalyzer(provider);

        const string text = "Nous proposons un acccompagnement personnalisé.";
        var hash = ContentAnalyzer.ComputeTextHash(text);

        var result = await analyzer.AnalyzeAsync(
            new List<(string, string)> { ("https://example.com/", text) },
            new HashSet<string> { hash });

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(1, result.PagesSkipped);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NoSpellingCandidates_NoLlmCallMade()
    {
        var provider = new FakeLlmProvider();
        var analyzer = CreateAnalyzer(provider);

        var result = await analyzer.AnalyzeAsync(
            new List<(string, string)>
            {
                ("https://example.com/", "Bienvenue sur notre site professionnel et sérieux."),
            },
            new HashSet<string>());

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task CostCeilingReached_StopsEarly_AndAddsIncompleteFinding()
    {
        // Chaque page part dans un lot séparé (BatchPages=1) pour forcer plusieurs appels.
        var provider = new FakeLlmProvider(
            new LlmResponse("[]", new LlmUsage(1_000_000, 1_000_000)), // ~ dépasse le plafond dès le 1er lot
            new LlmResponse("[]", new LlmUsage(1_000_000, 1_000_000)));

        var options = new ContentAnalyzerOptions
        {
            BatchPages = 1,
            MaxCostEur = 0.01m, // plafond très bas : atteint après le premier lot
            InputPricePerMTokEur = 2.8m,
            OutputPricePerMTokEur = 14m,
        };
        var analyzer = CreateAnalyzer(provider, options);

        var pages = new List<(string, string)>
        {
            ("https://example.com/a", "Une acccompagnement fautive ici."),
            ("https://example.com/b", "Une acccompagnement fautive ailleurs."),
            ("https://example.com/c", "Une acccompagnement fautive encore."),
        };

        var result = await analyzer.AnalyzeAsync(pages, new HashSet<string>());

        Assert.True(result.Truncated);
        Assert.True(provider.CallCount < pages.Count, "Le budget doit interrompre l'analyse avant la fin.");
        Assert.Contains(result.Findings, f => f.FindingId == "meta-analyse-incomplete");
    }

    [Fact]
    public void ParseFindings_HandlesMarkdownCodeFence()
    {
        const string response = """
        ```json
        [{"page": "https://example.com/", "extrait": "x", "probleme": "y", "severite": "basse"}]
        ```
        """;
        var items = ContentAnalyzer.ParseFindings(response);
        var item = Assert.Single(items);
        Assert.Equal("y", item.Probleme);
    }

    [Fact]
    public void ParseFindings_EmptyArray_ReturnsEmptyList()
    {
        Assert.Empty(ContentAnalyzer.ParseFindings("[]"));
    }

    [Fact]
    public void ParseFindings_InvalidJson_ReturnsEmptyList_DoesNotThrow()
    {
        Assert.Empty(ContentAnalyzer.ParseFindings("ceci n'est pas du JSON"));
    }
}
