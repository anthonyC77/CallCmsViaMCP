namespace SiteGuardian.Api.Services.Llm;

/// <summary>
/// Abstraction LLM (cf. §5 du plan) : permet de brancher plus tard un provider
/// local (Ollama) derrière la même interface que Claude.
/// </summary>
public interface ILlmProvider
{
    /// <summary>False si aucune clé API n'est configurée — l'analyse LLM est alors sautée.</summary>
    bool IsEnabled { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

public record LlmRequest(
    string Model,
    string SystemPrompt,
    string UserMessage,
    int MaxTokens = 4096);

public record LlmResponse(string Text, LlmUsage Usage);

public record LlmUsage(long InputTokens, long OutputTokens);

/// <summary>Provider inactif quand aucune clé n'est configurée (l'audit reste 100 % déterministe).</summary>
public sealed class DisabledLlmProvider : ILlmProvider
{
    public bool IsEnabled => false;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "Aucune clé API Anthropic configurée (Anthropic:ApiKey ou ANTHROPIC_API_KEY).");
}
