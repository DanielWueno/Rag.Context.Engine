namespace RagEngine.Core.Domain;

/// <summary>Identity of the bytes and inference settings that produced a reranked score.</summary>
public sealed record CrossEncoderIdentity
{
    public required string ModelSha256 { get; init; }
    public required string TokenizerSha256 { get; init; }
    public required string Binary { get; init; }
    public required string Architecture { get; init; }
    public required bool? StableGateScore { get; init; }
    public required int MaxSequenceLength { get; init; }
    public required int BatchSize { get; init; }

    internal void Validate()
    {
        static bool IsHash(string? value) =>
            value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c));

        if (!IsHash(ModelSha256) || !IsHash(TokenizerSha256) ||
            string.IsNullOrWhiteSpace(Binary) || Path.GetFileName(Binary) != Binary ||
            string.IsNullOrWhiteSpace(Architecture) || StableGateScore is null ||
            MaxSequenceLength <= 4 || BatchSize <= 0)
            throw new InvalidOperationException("Gate calibration requires complete cross-encoder identity (lowercase SHA-256, binary, architecture and inference settings).");
    }
}

/// <summary>Opt-in, indivisible binding between gate thresholds and a measured cross-encoder.</summary>
public sealed record GateCalibration
{
    public required CrossEncoderIdentity CrossEncoder { get; init; }
    public required float? LowConfidenceThreshold { get; init; }
    public required float? HighConfidenceThreshold { get; init; }

    public void ValidateFor(RetrievalResult winner)
    {
        if (CrossEncoder is null)
            throw new InvalidOperationException("Gate calibration is missing its cross-encoder identity.");
        CrossEncoder.Validate();
        if (LowConfidenceThreshold is not { } low || HighConfidenceThreshold is not { } high ||
            !float.IsFinite(low) || !float.IsFinite(high) || low < 0 || low >= high || high > 1)
            throw new InvalidOperationException("Gate calibration requires finite thresholds: 0 <= low < high <= 1.");

        var expectedScale = CrossEncoder.StableGateScore == true
            ? RetrievalScoreScale.CrossEncoderStable : RetrievalScoreScale.CrossEncoderBatched;
        if (winner.CrossEncoder != CrossEncoder || winner.ScoreScale != expectedScale)
            throw new InvalidOperationException(
                $"Gate calibration does not match the effective cross-encoder. Expected: {CrossEncoder}; " +
                $"actual: {winner.CrossEncoder?.ToString() ?? "(missing identity)"}; scale: {winner.ScoreScale}. " +
                "Restore the binary and its profile together, or recalibrate before using these thresholds.");
    }

    internal static IReadOnlyList<RetrievalResult> Apply(
        IReadOnlyList<RetrievalResult> results, GateCalibration? calibration)
    {
        if (calibration is null || results.Count == 0)
            return results;
        calibration.ValidateFor(results[0]);
        var calibrated = results.ToArray();
        // The gate consumes only position zero; the tail retains its batched scores.
        calibrated[0] = calibrated[0] with { GateCalibration = calibration };
        return calibrated;
    }
}
