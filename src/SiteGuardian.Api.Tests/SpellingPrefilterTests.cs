using SiteGuardian.Api.Services.Llm;

namespace SiteGuardian.Api.Tests;

public class SpellingPrefilterTests
{
    /// <summary>Remonte depuis le bin de test jusqu'à trouver Dictionaries/fr/fr.dic.</summary>
    private static string FindApiContentRoot()
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

    private static SpellingPrefilter CreatePrefilter() => SpellingPrefilter.CreateFrench(FindApiContentRoot());

    [Fact]
    public void IsEnabled_WhenDictionaryFound_IsTrue()
    {
        Assert.True(CreatePrefilter().IsEnabled);
    }

    [Fact]
    public void CorrectFrenchText_ProducesNoCandidates()
    {
        var prefilter = CreatePrefilter();
        var candidates = prefilter.FindCandidates(
            "Bienvenue sur notre site. Nous proposons un accompagnement personnalisé pour chaque patient.");
        Assert.Empty(candidates);
    }

    [Fact]
    public void MisspelledWord_IsDetectedWithContext()
    {
        var prefilter = CreatePrefilter();
        var candidates = prefilter.FindCandidates(
            "Nous proposons un acccompagnement personnalisé pour chaque patient.");

        var candidate = Assert.Single(candidates);
        Assert.Equal("acccompagnement", candidate.Word);
        Assert.Contains("acccompagnement", candidate.Context);
    }

    [Fact]
    public void CapitalizedUnknownWord_IsTreatedAsProperNoun_AndIgnored()
    {
        var prefilter = CreatePrefilter();
        // "Holoniis" est un nom propre inconnu du dictionnaire : ne doit pas remonter.
        var candidates = prefilter.FindCandidates("Bienvenue chez Holoniis, votre cabinet de confiance.");
        Assert.DoesNotContain(candidates, c => c.Word == "Holoniis");
    }

    [Fact]
    public void MaxCandidates_CapsResultCount()
    {
        var prefilter = CreatePrefilter();
        // Suffixe alphabétique (pas numérique : \p{L} exclut les chiffres, qui
        // couperaient le mot et feraient collapser tous les candidats en un seul).
        var suffixes = "abcdefghijklmnopqrstuvwxyz";
        var text = string.Join(" ", suffixes.Select(c => $"xzqwfvbkkk{c}"));
        var candidates = prefilter.FindCandidates(text, maxCandidates: 5);
        Assert.Equal(5, candidates.Count);
    }

    [Fact]
    public void EmptyText_ReturnsNoCandidates()
    {
        Assert.Empty(CreatePrefilter().FindCandidates(""));
    }

    [Fact]
    public void DisabledPrefilter_NullDictionary_AlwaysReturnsEmpty()
    {
        var prefilter = new SpellingPrefilter(null);
        Assert.False(prefilter.IsEnabled);
        Assert.Empty(prefilter.FindCandidates("acccompagnement est une faute evidente"));
    }
}
