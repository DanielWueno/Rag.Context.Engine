using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Re-scores a candidate pool of retrieval results against the original query
/// using a higher-precision model (e.g. a Cross-Encoder) and returns the final
/// Top-K ordered by the new relevance score.
///
/// A bi-encoder (dense embedding) compresses query and chunk independently and
/// compares them by cosine; a cross-encoder reads the (query, chunk) pair
/// jointly through full attention, so it can resolve interactions the
/// bi-encoder loses — at the cost of one inference per candidate. That is why
/// it runs only over the reduced pool that hybrid search already produced,
/// never over the whole collection.
/// </summary>
public interface IReRanker
{
    /// <summary>
    /// Scores each candidate against <paramref name="query"/> and returns the
    /// <paramref name="topK"/> best, ordered by descending relevance. The
    /// returned results carry the cross-encoder score in
    /// <see cref="RetrievalResult.SimilarityScore"/> (sigmoid, range 0..1) y lo
    /// declaran en <see cref="RetrievalResult.ScoreScale"/>.
    /// Empates exactos del score de ranking se resuelven por UUID de chunk canonico
    /// (formato D minusculas), en orden ordinal ascendente, antes de cortar TopK.
    /// <see cref="RetrievalResult.RankingScore"/> conserva ese score aunque cambie el gate.
    ///
    /// <para><b>El orden devuelto es el contrato; el score no lo reconstruye.</b> Una
    /// implementación puede re-puntuar la posición #0 en una escala distinta al resto —
    /// es lo que hace el ítem 4.2 para que el número que lee el gate de confianza no
    /// dependa del TopK: el #0 sale con
    /// <see cref="RetrievalScoreScale.CrossEncoderStable"/> y la cola con
    /// <see cref="RetrievalScoreScale.CrossEncoderBatched"/>. En ese caso la lista
    /// <b>no está ordenada monótonamente</b> por <c>SimilarityScore</c> y el #0 puede
    /// puntuar por debajo del #1. El llamador debe preservar el orden recibido:
    /// reordenarlo por score revierte 4.2 en silencio y devuelve al gate un número que
    /// vuelve a moverse con el TopK.</para>
    /// </summary>
    /// <param name="query">The original natural-language query.</param>
    /// <param name="candidates">Candidate pool from first-stage retrieval.</param>
    /// <param name="topK">Number of results to keep after re-scoring.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<IReadOnlyList<RetrievalResult>> ReRankAsync(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        int topK,
        CancellationToken cancellationToken = default);
}
