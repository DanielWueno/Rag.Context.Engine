using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// El modo de fallo que estos tests cubren es real y está en el servidor de desarrollo:
/// la colección 'engine-repo' se creó con un vector anónimo, se lista como sana ('green',
/// 304 puntos) y revienta en la consulta con "Not existing vector name error: dense".
/// Contarla como una colección más —lo que hacía el doctor— es reportar salud sobre algo
/// inservible.
///
/// La clasificación se prueba como función pura porque el escenario de riesgo no se puede
/// montar de otra forma: para tener un test de integración con una colección rota habría
/// que crear a mano un esquema que el motor ya no sabe crear.
/// </summary>
public class CollectionSchemaDiagnosticsTests
{
    private const int BrainDimensions = 384;

    private static CollectionSchemaSnapshot Snapshot(
        string name,
        bool usesNamedVectors = true,
        Dictionary<string, ulong>? dense = null,
        string[]? sparse = null,
        ulong? anonymousSize = null,
        ulong points = 100) =>
        new(name,
            usesNamedVectors,
            dense ?? new Dictionary<string, ulong>(),
            sparse ?? Array.Empty<string>(),
            anonymousSize,
            points);

    private static Dictionary<string, ulong> Dense(bool conResumen = false)
    {
        var map = new Dictionary<string, ulong> { [QdrantVectorStore.DenseVectorName] = BrainDimensions };
        if (conResumen)
        {
            map[QdrantVectorStore.SummaryVectorName] = BrainDimensions;
        }

        return map;
    }

    private static string[] Sparse() => new[] { QdrantVectorStore.SparseVectorName };

    // ── El escenario de riesgo: la colección que hoy revienta en la consulta ──────────

    [Fact]
    public void VectorAnonimo_EsIncompatible_YExplicaElErrorQueVeElUsuario()
    {
        // Esquema literal de 'engine-repo' leído del servidor: {"size":384,...} sin nombre y
        // sin sparse.
        var report = CollectionSchemaDiagnostics.Classify(
            Snapshot("engine-repo", usesNamedVectors: false, anonymousSize: 384, points: 304),
            BrainDimensions);

        Assert.Equal(CollectionSchemaStatus.Incompatible, report.Status);
        Assert.Contains(report.Problems, p => p.Contains("Not existing vector name error: dense"));
        Assert.NotNull(report.Remedy);
        Assert.Contains("engine-repo", report.Remedy);
        Assert.Equal(304UL, report.PointsCount);
    }

    [Fact]
    public void SinVectorSparse_EsIncompatible_PorqueLaBusquedaEsHibrida()
    {
        var report = CollectionSchemaDiagnostics.Classify(
            Snapshot("solo-densa", dense: Dense(), sparse: Array.Empty<string>()),
            BrainDimensions);

        Assert.Equal(CollectionSchemaStatus.Incompatible, report.Status);
        Assert.Contains(report.Problems, p => p.Contains(QdrantVectorStore.SparseVectorName));
    }

    [Fact]
    public void DenseConOtroNombre_EsIncompatible_YListaLosQueSiEstan()
    {
        var report = CollectionSchemaDiagnostics.Classify(
            Snapshot("otro-motor",
                dense: new Dictionary<string, ulong> { ["text-dense"] = BrainDimensions },
                sparse: Sparse()),
            BrainDimensions);

        Assert.Equal(CollectionSchemaStatus.Incompatible, report.Status);
        Assert.Contains(report.Problems, p => p.Contains("text-dense"));
    }

    [Fact]
    public void DimensionDistintaALaDelModelo_EsIncompatible()
    {
        // Este es el fallo silencioso: Qdrant sí tiene un vector llamado 'dense', así que la
        // consulta no falla por nombre — falla por tamaño, o peor, no falla y compara
        // embeddings de otro modelo. Un doctor que sólo mire nombres no lo ve.
        var report = CollectionSchemaDiagnostics.Classify(
            Snapshot("de-otro-modelo",
                dense: new Dictionary<string, ulong> { [QdrantVectorStore.DenseVectorName] = 768 },
                sparse: Sparse()),
            BrainDimensions);

        Assert.Equal(CollectionSchemaStatus.Incompatible, report.Status);
        Assert.Contains(report.Problems, p => p.Contains("768") && p.Contains("384"));
    }

    // ── Lo que NO debe marcarse como roto ────────────────────────────────────────────

    [Fact]
    public void DenseYSparseSinResumen_EsLegacy_NoIncompatible()
    {
        // Esquema literal de 'micro-repo', 'innovapp-docs' y 'wiki-solis'. El tercer vector es
        // opt-in por colección, no un requisito: marcar esto como roto sería mandar a re-ingestar
        // corpus que funcionan.
        var report = CollectionSchemaDiagnostics.Classify(
            Snapshot("micro-repo", dense: Dense(), sparse: Sparse(), points: 953),
            BrainDimensions);

        Assert.Equal(CollectionSchemaStatus.Legacy, report.Status);
        Assert.Empty(report.Problems);
        Assert.Contains(report.Notes, n => n.Contains(QdrantVectorStore.SummaryVectorName));
        Assert.Null(report.Remedy);
    }

    [Fact]
    public void EsquemaCompleto_EsCurrent_YNoSugiereNada()
    {
        var report = CollectionSchemaDiagnostics.Classify(
            Snapshot("bsuite-repo", dense: Dense(conResumen: true), sparse: Sparse(), points: 22592),
            BrainDimensions);

        Assert.Equal(CollectionSchemaStatus.Current, report.Status);
        Assert.Empty(report.Problems);
        Assert.Empty(report.Notes);
        Assert.Null(report.Remedy);
    }

    // ── Diagnóstico sin modelo cargado ───────────────────────────────────────────────

    [Fact]
    public void SinDimensionEsperada_SigueDetectandoLaColeccionRota()
    {
        // Cuando el ONNX no carga (exit 134 en ARM, modelo ausente) el doctor todavía tiene que
        // nombrar la colección rota: es cuando más falta hace.
        var report = CollectionSchemaDiagnostics.Classify(
            Snapshot("engine-repo", usesNamedVectors: false, anonymousSize: 384),
            expectedDimension: null);

        Assert.Equal(CollectionSchemaStatus.Incompatible, report.Status);
    }

    [Fact]
    public void SinDimensionEsperada_NoInventaUnFalloDeDimension()
    {
        var report = CollectionSchemaDiagnostics.Classify(
            Snapshot("de-otro-modelo",
                dense: new Dictionary<string, ulong> { [QdrantVectorStore.DenseVectorName] = 768 },
                sparse: Sparse()),
            expectedDimension: null);

        Assert.Equal(CollectionSchemaStatus.Legacy, report.Status);
        Assert.Empty(report.Problems);
    }
}
