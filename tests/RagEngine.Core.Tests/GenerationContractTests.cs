using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using Xunit;
using static RagEngine.Core.Tests.GenerationHttpHarnessTests;

namespace RagEngine.Core.Tests;

public sealed class GenerationContractTests
{
    [Theory]
    [InlineData("high", GroundingVerdict.High, 3)]
    [InlineData("mid", GroundingVerdict.Medium, 3)]
    [InlineData("low", GroundingVerdict.Ungrounded, 0)]
    [InlineData("empty", GroundingVerdict.Ungrounded, 0)]
    [InlineData("nonmonotonic", GroundingVerdict.Medium, 3)]
    [InlineData("rrf", GroundingVerdict.High, 3)]
    [InlineData("meta", GroundingVerdict.NotEvaluated, 0)]
    public async Task Context_precedes_generation_and_exposes_the_actual_gate_decision(
        string query, GroundingVerdict verdict, int sources)
    {
        await using var factory = new GenerationFactory();
        using var scope = factory.Services.CreateScope();
        var generation = scope.ServiceProvider.GetRequiredService<IRagGenerationService>();
        var context = RetrievalContext.ForAuthorized("tenant-a", "module-a");
        await using var turn = generation.AskStreamingAsync(query, "fixture", context,
            topK: 3, minimumScore: 0.2f, useReRanking: true, responseMode: ResponseMode.Technical)
            .GetAsyncEnumerator();

        Assert.True(await turn.MoveNextAsync());
        var ready = Assert.IsType<GenerationEvent.ContextReady>(turn.Current);
        Assert.Equal(verdict, ready.Grounding);
        Assert.Equal(sources, ready.Sources.Count);
        Assert.Empty(factory.Chat.Prompts);
        if (sources > 0)
            Assert.Equal(new[] { "code", "docs", "sentinel" }, ready.Sources.Select(s => s.ChunkId));

        var remainder = new List<GenerationEvent>();
        while (await turn.MoveNextAsync())
            remainder.Add(turn.Current);
        Assert.All(remainder.SkipLast(1), item => Assert.IsType<GenerationEvent.TextDelta>(item));
        Assert.Equal(GenerationOutcome.Answered, Assert.IsType<GenerationEvent.Completed>(remainder[^1]).Outcome);
        Assert.Equal(query == "meta" ? 0 : 1, factory.Retriever.Calls);
        if (query != "meta")
        {
            Assert.Equal(query, factory.Retriever.LastQuery);
            Assert.Equal(context, factory.Retriever.LastOptions!.Context);
            Assert.Equal("fixture", factory.Retriever.LastOptions.CollectionName);
            Assert.Equal(3, factory.Retriever.LastOptions.TopK);
            Assert.Equal(0.2f, factory.Retriever.LastOptions.MinimumSimilarityScore);
            Assert.True(factory.Retriever.LastOptions.UseReRanking);
        }
    }

    [Theory]
    [InlineData("fallback", GenerationOutcome.ModelDeclined)]
    [InlineData("fallback-near", GenerationOutcome.Answered)]
    public async Task Completion_recognizes_only_exact_model_decline_across_fragments(
        string query, GenerationOutcome outcome)
    {
        await using var factory = new GenerationFactory();
        using var scope = factory.Services.CreateScope();
        var generation = scope.ServiceProvider.GetRequiredService<IRagGenerationService>();
        var events = new List<GenerationEvent>();
        await foreach (var update in generation.AskStreamingAsync(query, "fixture", RetrievalContext.Local,
            responseMode: ResponseMode.Technical))
            events.Add(update);
        Assert.Equal(3, Assert.IsType<GenerationEvent.ContextReady>(events[0]).Sources.Count);
        Assert.Equal(2, events.OfType<GenerationEvent.TextDelta>().Count());
        Assert.Equal(outcome, Assert.IsType<GenerationEvent.Completed>(events[^1]).Outcome);
    }

    [Fact]
    public async Task Cancellation_after_meta_text_does_not_emit_completed()
    {
        await using var factory = new GenerationFactory();
        using var scope = factory.Services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        await using var turn = scope.ServiceProvider.GetRequiredService<IRagGenerationService>()
            .AskStreamingAsync("meta", "fixture", RetrievalContext.Local)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await turn.MoveNextAsync());
        Assert.True(await turn.MoveNextAsync());
        Assert.IsType<GenerationEvent.TextDelta>(turn.Current);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await turn.MoveNextAsync());
    }

    [Fact]
    public void Hosts_cannot_recalculate_grounding_or_depend_on_the_concrete_service()
    {
        foreach (var host in new[] { "RagEngine.Api", "RagEngine.Cli" })
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src", host), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains("/obj/") && !file.Contains("/bin/")))
        {
            Assert.DoesNotMatch(@"\b(RagGenerationService|ConfidenceGate|IMetaIntentDetector|NoContextFallbackMessage)\b",
                File.ReadAllText(file));
        }
        var project = File.ReadAllText(Path.Combine(Root, "src/RagEngine.Core/RagEngine.Core.csproj"));
        Assert.DoesNotContain("<_Parameter1>RagEngine.Api</_Parameter1>", project);
        var api = File.ReadAllText(Path.Combine(Root, "src/RagEngine.Api/Program.cs"));
        Assert.Single(Regex.Matches(api, @"\bSearchAsync\s*\(")); // /api/search only
    }
}
