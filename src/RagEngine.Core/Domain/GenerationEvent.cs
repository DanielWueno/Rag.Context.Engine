namespace RagEngine.Core.Domain;

public enum GroundingVerdict
{
    NotEvaluated,
    Ungrounded,
    Medium,
    High
}

public enum GenerationOutcome
{
    Answered,
    ModelDeclined
}

/// <summary>
/// One turn emits ContextReady, then text fragments, then Completed. Errors and
/// cancellation propagate without Completed. Sources retain retrieval order and
/// are empty for meta-intent or ungrounded turns. ModelDeclined retracts sources
/// after generation without requiring hosts to recognize a prompt's fallback text.
/// </summary>
public abstract record GenerationEvent
{
    private GenerationEvent() { }

    public sealed record ContextReady(
        IReadOnlyList<RetrievalResult> Sources, GroundingVerdict Grounding) : GenerationEvent;

    public sealed record TextDelta(string Text) : GenerationEvent;

    public sealed record Completed(GenerationOutcome Outcome) : GenerationEvent;
}
