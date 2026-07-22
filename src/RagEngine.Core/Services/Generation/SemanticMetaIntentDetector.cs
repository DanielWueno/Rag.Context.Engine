using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;

namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Detects meta-questions about the assistant itself by semantic similarity
/// against a small exemplar set, using the same dense embedder already loaded
/// for retrieval (<see cref="IVectorizationBrain"/>) — no extra model, no LLM
/// call. Replaces an earlier regex-based version: a hand-written pattern list
/// only ever covers phrasings already seen in logs, so it grows without bound
/// as new ways of asking "who are you?" show up; semantic similarity generalizes
/// to paraphrases of the same intent without code changes.
///
/// Registered as a singleton (see <c>GenerationServiceExtensions</c>): the
/// exemplar embeddings are computed once, lazily, on first use, and reused for
/// the process lifetime — the same lifecycle already used for
/// <see cref="IVectorizationBrain"/> itself.
/// </summary>
public sealed class SemanticMetaIntentDetector : IMetaIntentDetector
{
    /// <summary>
    /// Closed-ish set of canonical phrasings for meta-questions about the
    /// assistant, grouped by sub-intent (identity, tech stack, training,
    /// language, hallucination/reliability, indexed-file count, how it works).
    /// Several variants per category, not one — a single exemplar per category
    /// is brittle; matching against the max similarity across variants is more
    /// robust to phrasing than any single canonical sentence. Extending this
    /// list (occasionally, for a genuinely new sub-intent) is far cheaper than
    /// the regex list it replaced, since paraphrases of an existing entry don't
    /// need a new one.
    /// </summary>
    public static readonly string[] Exemplars =
    [
        "¿Quién eres?",
        "¿Quién eres tú?",
        "Preséntate",
        "¿Qué eres exactamente?",
        "¿Qué proyecto analizas?",
        "¿Qué tecnología usas?",
        "¿Qué modelo eres?",
        "¿Con qué tecnología está hecho este chat?",
        "¿Con qué estás entrenado?",
        "¿Con qué fuiste construido?",
        "¿Cómo estás hecho?",
        "¿Qué modelo de lenguaje corre por debajo?",
        "¿En qué idioma puedes responder?",
        "¿En qué idiomas hablas?",
        "¿Alucinas?",
        "¿Puedes inventar información?",
        "¿Qué tan confiable eres?",
        "¿Cuántos archivos te constituyen?",
        "¿Cuántos documentos tienes indexados?",
        "¿Cuántos archivos conforman este sistema?",
        "¿Cómo es que puedes responderme?",
        "¿Cómo funcionas?",
        "¿Cómo generas tus respuestas?",
        "Explícame cómo trabajas",
    ];

    private readonly IVectorizationBrain _brain;
    private readonly MetaIntentOptions _options;
    private readonly Lazy<Task<float[][]>> _exemplarEmbeddings;

    public SemanticMetaIntentDetector(IVectorizationBrain brain, IOptions<MetaIntentOptions> options)
    {
        _brain = brain ?? throw new ArgumentNullException(nameof(brain));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _exemplarEmbeddings = new Lazy<Task<float[][]>>(EmbedExemplarsAsync);
    }

    private async Task<float[][]> EmbedExemplarsAsync()
    {
        var embeddings = await _brain.GenerateBatchEmbeddingsAsync(Exemplars);
        return embeddings.ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> IsMetaIntentAsync(string query, CancellationToken cancellationToken = default)
    {
        var exemplarEmbeddings = await _exemplarEmbeddings.Value;
        var queryEmbedding = await _brain.GenerateEmbeddingAsync(query, cancellationToken);

        var maxSimilarity = 0f;
        foreach (var exemplar in exemplarEmbeddings)
        {
            var similarity = Dot(queryEmbedding, exemplar);
            if (similarity > maxSimilarity)
                maxSimilarity = similarity;
        }

        return maxSimilarity >= _options.SimilarityThreshold;
    }

    // Both vectors are already L2-normalized by IVectorizationBrain, so the dot
    // product IS the cosine similarity — no extra normalization needed here.
    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }
}
