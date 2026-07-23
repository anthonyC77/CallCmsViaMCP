using SiteGuardian.Api.Services.Llm;

namespace SiteGuardian.Api.Tests.Fakes;

/// <summary>Provider de test : retourne des réponses préprogrammées, une par appel.</summary>
public class FakeLlmProvider : ILlmProvider
{
    private readonly Queue<LlmResponse> _responses;

    public FakeLlmProvider(params LlmResponse[] responses) => _responses = new Queue<LlmResponse>(responses);

    public bool IsEnabled => true;

    public int CallCount { get; private set; }

    public List<string> ReceivedMessages { get; } = new();

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        CallCount++;
        ReceivedMessages.Add(request.UserMessage);
        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new LlmResponse("[]", new LlmUsage(0, 0));
        return Task.FromResult(response);
    }
}
