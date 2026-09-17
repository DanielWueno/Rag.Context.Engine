using System.Text.Json;
using Xunit;
using static RagEngine.Core.Tests.GenerationHttpHarnessTests;

namespace RagEngine.Core.Tests;

public sealed class GenerationQualityTests
{
    [Fact]
    public async Task Quality_bundle_preserves_answers_sources_and_llm_inputs_with_one_real_search()
    {
        await using var factory = new GenerationFactory(realRetrieval: true);
        using var http = factory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        var captures = new List<Capture>();
        var files = Directory.GetFiles(Path.Combine(Root, "replicate-env/data/questions"), "*.json")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(7, files.Length);
        foreach (var file in files)
        {
            var collection = Path.GetFileNameWithoutExtension(file);
            var items = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(file))!;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var query = item.GetProperty("query").GetString()!.Trim();
                if (query.Length == 0 || !seen.Add(query))
                    continue;
                var request = new
                {
                    query, collection,
                    topK = item.TryGetProperty("topK", out var k) && k.ValueKind == JsonValueKind.Number && k.GetInt32() != 0 ? k.GetInt32() : 10,
                    minScore = item.TryGetProperty("minScore", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetSingle() : 0.1f,
                    rerank = !item.TryGetProperty("rerank", out var rerank) || rerank.ValueKind == JsonValueKind.Null || rerank.GetBoolean(),
                    responseMode = item.TryGetProperty("responseMode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null
                };
                var capture = await CaptureAsync(factory, http, "/api/ask", request);
                Assert.Equal(collection == "bsuite-auditorias-baseline" ? 500 : 200, capture.Status);
                captures.Add(capture);
            }
        }
        CompareOrCapture("quality.json", captures);
    }
}
