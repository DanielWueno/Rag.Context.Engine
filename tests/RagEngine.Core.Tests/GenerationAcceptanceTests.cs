using Xunit;
using static RagEngine.Core.Tests.GenerationHttpHarnessTests;

namespace RagEngine.Core.Tests;

public sealed class GenerationAcceptanceTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("body")]
    [InlineData("prompt")]
    [InlineData("request")]
    [InlineData("status")]
    [InlineData("calls")]
    [InlineData("measurements")]
    [InlineData("meta")]
    public void Comparator_rejects_missing_or_changed_evidence(string mutation)
    {
        var control = new Capture("request", 200, "answer and sources", ["prompt"], 2, 2, 2);
        var candidate = control with { SearchCalls = 1, SearchMeasurements = 1, MetaCalls = 1 };
        AssertEquivalent([control], [candidate]);
        var changed = mutation switch
        {
            "body" => candidate with { Body = "different answer or sources" },
            "prompt" => candidate with { Prompts = ["different prompt"] },
            "request" => candidate with { Request = "different request" },
            "status" => candidate with { Status = 500 },
            "calls" => candidate with { SearchCalls = 2 },
            "measurements" => candidate with { SearchMeasurements = 0 },
            "meta" => candidate with { MetaCalls = 2 },
            _ => candidate
        };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertEquivalent([control], mutation == "missing" ? [] : [changed]));
    }
}
