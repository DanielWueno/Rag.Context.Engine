using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;
using RagEngine.Core.Services.Generation;
using Xunit;

namespace RagEngine.Core.Tests;

public sealed class GateCalibrationTests
{
    internal static readonly CrossEncoderIdentity Identity = new()
    {
        ModelSha256 = new string('a', 64), TokenizerSha256 = new string('b', 64),
        Binary = "model_qint8_arm64.onnx", Architecture = "arm64", StableGateScore = true,
        MaxSequenceLength = 512, BatchSize = 8
    };

    internal static readonly GateCalibration Calibration = new()
    {
        CrossEncoder = Identity, LowConfidenceThreshold = .2f, HighConfidenceThreshold = .8f
    };

    internal static RetrievalResult Winner(float score = .7f) =>
        GenerationHttpHarnessTests.RecordingRetriever.Chunks("high")[0] with
        {
            SimilarityScore = score, CrossEncoder = Identity
        };

    internal static ConfidenceGate Gate() => new(
        new FixedOptions(), NullLogger<ConfidenceGate>.Instance);

    [Fact]
    public void Profile_binding_keeps_identity_and_thresholds_together()
    {
        var values = new Dictionary<string, string?>
        {
            ["RetrievalProfiles:Profiles:strict:GateCalibration:LowConfidenceThreshold"] = "0.2",
            ["RetrievalProfiles:Profiles:strict:GateCalibration:HighConfidenceThreshold"] = "0.8"
        };
        var prefix = "RetrievalProfiles:Profiles:strict:GateCalibration:CrossEncoder:";
        values[prefix + "ModelSha256"] = Identity.ModelSha256;
        values[prefix + "TokenizerSha256"] = Identity.TokenizerSha256;
        values[prefix + "Binary"] = Identity.Binary;
        values[prefix + "Architecture"] = Identity.Architecture;
        values[prefix + "StableGateScore"] = "true";
        values[prefix + "MaxSequenceLength"] = "512";
        values[prefix + "BatchSize"] = "8";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var profile = config.GetSection("RetrievalProfiles").Get<RetrievalProfileCatalogOptions>()!.Profiles["strict"];
        Assert.Equal(Calibration, profile.GateCalibration);
        profile.GateCalibration!.ValidateFor(Winner());

        values.Remove(prefix + "StableGateScore");
        var incomplete = new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection("RetrievalProfiles").Get<RetrievalProfileCatalogOptions>()!.Profiles["strict"];
        Assert.Throws<InvalidOperationException>(() => incomplete.GateCalibration!.ValidateFor(Winner()));
    }

    public static TheoryData<CrossEncoderIdentity> IncompatibleIdentities => new()
    {
        Identity with { ModelSha256 = new string('c', 64) },
        Identity with { TokenizerSha256 = new string('c', 64) },
        Identity with { Binary = "model.onnx" },
        Identity with { Architecture = "x64" },
        Identity with { StableGateScore = false },
        Identity with { MaxSequenceLength = 256 },
        Identity with { BatchSize = 4 }
    };

    [Theory]
    [MemberData(nameof(IncompatibleIdentities))]
    public void Incompatible_loaded_identity_is_rejected_even_with_same_filename(CrossEncoderIdentity actual)
    {
        var winner = Winner() with { CrossEncoder = actual, GateCalibration = Calibration };
        Assert.Throws<InvalidOperationException>(() => GateCalibration.Apply([winner], Calibration));
        var error = Assert.Throws<InvalidOperationException>(() => Gate().Assess([winner], .1f, "q"));
        Assert.Contains("Restore the binary and its profile together", error.Message);
    }

    [Fact]
    public void Missing_identity_and_inconsistent_score_scale_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Calibration.ValidateFor(Winner() with { CrossEncoder = null }));
        Assert.Throws<InvalidOperationException>(() => Calibration.ValidateFor(
            Winner() with { ScoreScale = RetrievalScoreScale.CrossEncoderBatched }));
        Assert.Throws<InvalidOperationException>(() =>
            (Calibration with { CrossEncoder = Identity with { ModelSha256 = "partial" } }).ValidateFor(Winner()));
    }

    [Theory]
    [InlineData(-.1f, .8f)]
    [InlineData(.2f, 1.1f)]
    [InlineData(.2f, .2f)]
    [InlineData(.8f, .2f)]
    [InlineData(float.NaN, .8f)]
    [InlineData(.2f, float.PositiveInfinity)]
    [InlineData(null, .8f)]
    [InlineData(.2f, null)]
    public void Invalid_or_incomplete_thresholds_are_rejected(float? low, float? high) =>
        Assert.Throws<InvalidOperationException>(() =>
            (Calibration with { LowConfidenceThreshold = low, HighConfidenceThreshold = high }).ValidateFor(Winner()));

    [Fact]
    public void Profile_threshold_boundaries_do_not_mutate_global_gate_or_ranking()
    {
        var gate = Gate();
        foreach (var (score, expected) in new[]
        {
            (MathF.BitDecrement(.2f), GroundingVerdict.Ungrounded),
            (.2f, GroundingVerdict.Medium), (MathF.BitIncrement(.2f), GroundingVerdict.Medium),
            (MathF.BitDecrement(.8f), GroundingVerdict.Medium),
            (.8f, GroundingVerdict.High), (MathF.BitIncrement(.8f), GroundingVerdict.High)
        })
        {
            IReadOnlyList<RetrievalResult> source = [Winner(score), Winner(.95f) with { ChunkId = "tail" }];
            var applied = GateCalibration.Apply(source, Calibration);
            Assert.Equal(expected, gate.Assess(applied, .1f, "q").Verdict);
            Assert.Equal(source.Select(r => (r.ChunkId, r.SimilarityScore, r.RankingScore)),
                applied.Select(r => (r.ChunkId, r.SimilarityScore, r.RankingScore)));
            Assert.Null(source[0].GateCalibration);
            Assert.Same(source[1], applied[1]);
        }
        Assert.Equal(GroundingVerdict.High, gate.Assess([Winner()], .1f, "q").Verdict);
        Assert.Equal(GroundingVerdict.Medium, gate.Assess(GateCalibration.Apply([Winner()], Calibration), .1f, "q").Verdict);
        Assert.Equal(GroundingVerdict.High, gate.Assess([Winner()], .1f, "q").Verdict);
    }

    [Fact]
    public void Unprofiled_empty_and_nonreranked_paths_preserve_baseline_without_model()
    {
        IReadOnlyList<RetrievalResult> source = [Winner() with { CrossEncoder = null }];
        Assert.Same(source, GateCalibration.Apply(source, null));
        Assert.Equal(GroundingVerdict.High, Gate().Assess(source, .1f, "q").Verdict);
        Assert.Equal(GroundingVerdict.Ungrounded, Gate().Assess(GateCalibration.Apply([], Calibration), .1f, "q").Verdict);
        var rrf = Winner(.001f) with
        {
            ScoreScale = RetrievalScoreScale.RankFusionNative, CrossEncoder = null, GateCalibration = Calibration
        };
        Assert.Equal(GroundingVerdict.High, Gate().Assess([rrf], .1f, "q").Verdict);
    }

    private sealed class FixedOptions : IOptionsMonitor<RagGenerationOptions>
    {
        public RagGenerationOptions CurrentValue { get; } = new();
        public RagGenerationOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RagGenerationOptions, string?> listener) => null;
    }
}
