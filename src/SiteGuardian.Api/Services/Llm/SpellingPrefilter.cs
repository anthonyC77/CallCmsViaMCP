using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace SiteGuardian.Api.Services.Llm;

/// <summary>
/// Pré-filtre orthographique local (0 €, cf. §11 du plan) : Hunspell FR détecte
/// les mots suspects ; seuls ces candidats (avec contexte) partent au LLM —
/// jamais les pages entières.
/// </summary>
public class SpellingPrefilter
{
    private static readonly Regex WordRegex = new(@"[\p{L}][\p{L}'’-]{3,}", RegexOptions.Compiled);

    private readonly WordList? _dictionary;

    public SpellingPrefilter(WordList? dictionary) => _dictionary = dictionary;

    public bool IsEnabled => _dictionary is not null;

    /// <summary>Charge le dictionnaire FR depuis le dossier Dictionaries (null si absent).</summary>
    public static SpellingPrefilter CreateFrench(string contentRoot, ILogger? logger = null)
    {
        string[] roots = { contentRoot, AppContext.BaseDirectory };
        foreach (var root in roots)
        {
            var dic = Path.Combine(root, "Dictionaries", "fr", "fr.dic");
            var aff = Path.Combine(root, "Dictionaries", "fr", "fr.aff");
            if (File.Exists(dic) && File.Exists(aff))
                return new SpellingPrefilter(WordList.CreateFromFiles(dic, aff));
        }
        logger?.LogWarning("Dictionnaire Hunspell FR introuvable : pré-filtre orthographique désactivé.");
        return new SpellingPrefilter(null);
    }

    /// <summary>
    /// Retourne les mots absents du dictionnaire, avec un extrait de contexte,
    /// plafonnés à <paramref name="maxCandidates"/> par page.
    /// </summary>
    public List<SpellingCandidate> FindCandidates(string text, int maxCandidates = 30)
    {
        if (_dictionary is null || string.IsNullOrWhiteSpace(text))
            return new List<SpellingCandidate>();

        var candidates = new List<SpellingCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in WordRegex.Matches(text))
        {
            if (candidates.Count >= maxCandidates) break;

            var word = match.Value;
            if (!seen.Add(word)) continue;

            // Mot connu tel quel ou en minuscules (mots en début de phrase) → OK.
            if (_dictionary.Check(word) || _dictionary.Check(word.ToLowerInvariant()))
                continue;

            // Heuristique noms propres : mot capitalisé inconnu = probablement un nom → ignoré.
            if (char.IsUpper(word[0]))
                continue;

            candidates.Add(new SpellingCandidate(word, ExtractContext(text, match.Index, match.Length)));
        }

        return candidates;
    }

    private static string ExtractContext(string text, int index, int length, int radius = 60)
    {
        var start = Math.Max(0, index - radius);
        var end = Math.Min(text.Length, index + length + radius);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }
}

public record SpellingCandidate(string Word, string Context);
