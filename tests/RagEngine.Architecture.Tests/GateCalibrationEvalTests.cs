using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RagEngine.Cli.Commands;
using RagEngine.Cli.Infrastructure;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using Spectre.Console.Cli;
using Xunit;

namespace RagEngine.Architecture.Tests;

public sealed class GateCalibrationEvalTests
{
    private static readonly CrossEncoderIdentity Identity = new()
    {
        ModelSha256 = new string('a', 64), TokenizerSha256 = new string('b', 64),
        Binary = "effective.onnx", Architecture = "arm64", StableGateScore = true,
        MaxSequenceLength = 512, BatchSize = 8
    };

    [Fact]
    public void Eval_serializes_effective_identity_and_profile_not_configured_filename()
    {
        var (exit, json) = Run(rerank: true);
        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(json);
        var provenance = document.RootElement.GetProperty("provenance");
        Assert.Equal("effective.onnx", provenance.GetProperty("cross_encoder_model").GetString());
        Assert.Equal(Identity.ModelSha256, provenance.GetProperty("cross_encoder").GetProperty("model_sha256").GetString());
        Assert.Equal(.2f, provenance.GetProperty("gate_calibration").GetProperty("low_confidence_threshold").GetSingle());
        foreach (var result in document.RootElement.GetProperty("results").EnumerateArray())
        {
            Assert.Equal("CrossEncoderStable", result.GetProperty("score_scale").GetString());
            Assert.Equal(Identity.ModelSha256, result.GetProperty("cross_encoder").GetProperty("model_sha256").GetString());
        }

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var actual = provenance.Deserialize<EvalProvenance>(options)!;
        var missing = actual with { CrossEncoder = null };
        var changed = actual with { CrossEncoder = Identity with { ModelSha256 = new string('c', 64) } };
        Assert.NotEqual(actual.ComparabilityKeys()["cross_encoder_identity"], missing.ComparabilityKeys()["cross_encoder_identity"]);
        Assert.NotEqual(actual.ComparabilityKeys()["cross_encoder_identity"], changed.ComparabilityKeys()["cross_encoder_identity"]);
    }

    [Fact]
    public void Eval_without_rerank_needs_no_model_and_keeps_identity_absent()
    {
        var (exit, json) = Run(rerank: false);
        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(json);
        var provenance = document.RootElement.GetProperty("provenance");
        Assert.Equal(JsonValueKind.Null, provenance.GetProperty("cross_encoder").ValueKind);
        Assert.Equal(JsonValueKind.Null, provenance.GetProperty("gate_calibration").ValueKind);
    }

    [Fact]
    public void Eval_rejects_mixed_binary_provenance_in_one_run()
    {
        var (exit, _) = Run(rerank: true, changeIdentity: true);
        Assert.Equal(1, exit);
    }

    private static (int Exit, string Json) Run(bool rerank, bool changeIdentity = false)
    {
        var file = Path.Combine(Path.GetTempPath(), $"rag-gate-eval-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """
            [
              {"question":"q1","category":"fixture","sourceFile":"source.md","targetContentContains":["answer"]},
              {"question":"q2","category":"fixture","sourceFile":"source.md","targetContentContains":["answer"]}
            ]
            """);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<ISemanticRetriever>(new Retriever(changeIdentity));
            services.AddTransient<EvalCommand>();
            using var provider = services.BuildServiceProvider();
            var app = new CommandApp(new SpectreHostTypeRegistrar(provider));
            app.Configure(config =>
            {
                config.AddCommand<EvalCommand>("eval");
                config.SetExceptionHandler((_, _) => 1);
            });
            Console.SetOut(output);
            string[] arguments = ["eval", "--eval-set", file, "--json", .. rerank ? new[] { "--rerank" } : []];
            return (app.Run(arguments), output.ToString());
        }
        finally
        {
            Console.SetOut(original);
            File.Delete(file);
        }
    }

    private sealed class Retriever(bool changeIdentity) : ISemanticRetriever
    {
        public Task<IReadOnlyList<RetrievalResult>> SearchAsync(string query, RetrievalOptions options,
            CancellationToken cancellationToken = default)
        {
            var identity = changeIdentity && query == "q2" ? Identity with { ModelSha256 = new string('c', 64) } : Identity;
            var result = new RetrievalResult("1", "answer", .7f,
                options.UseReRanking ? RetrievalScoreScale.CrossEncoderStable : RetrievalScoreScale.RankFusionNative,
                new CodeChunkMetadata("/repo/source.md", "source.md", SourceLanguage.Markdown,
                    null, null, null, 1, 2, DateTimeOffset.UnixEpoch, "fixture"), "hash")
            {
                CrossEncoder = options.UseReRanking ? identity : null,
                GateCalibration = options.UseReRanking ? new GateCalibration
                {
                    CrossEncoder = identity, LowConfidenceThreshold = .2f, HighConfidenceThreshold = .8f
                } : null
            };
            return Task.FromResult<IReadOnlyList<RetrievalResult>>([result]);
        }
    }
}
