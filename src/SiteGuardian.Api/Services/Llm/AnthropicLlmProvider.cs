using System.Text;
using Anthropic;
using Anthropic.Models.Messages;

namespace SiteGuardian.Api.Services.Llm;

/// <summary>
/// Implémentation Claude via le SDK C# officiel (package NuGet « Anthropic »).
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private readonly AnthropicClient _client;
    private readonly ILogger<AnthropicLlmProvider> _logger;

    public AnthropicLlmProvider(string apiKey, ILogger<AnthropicLlmProvider> logger)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
        _logger = logger;
    }

    public bool IsEnabled => true;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var message = await _client.Messages.Create(
            new MessageCreateParams
            {
                Model = request.Model,
                MaxTokens = request.MaxTokens,
                System = request.SystemPrompt,
                Messages =
                [
                    new MessageParam { Role = Role.User, Content = request.UserMessage },
                ],
            },
            cancellationToken: ct);

        var sb = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock))
                sb.Append(textBlock.Text);
        }

        var usage = new LlmUsage(message.Usage.InputTokens, message.Usage.OutputTokens);
        _logger.LogInformation(
            "Appel LLM {Model} : {In} tokens entrée, {Out} tokens sortie",
            request.Model, usage.InputTokens, usage.OutputTokens);

        return new LlmResponse(sb.ToString(), usage);
    }
}
