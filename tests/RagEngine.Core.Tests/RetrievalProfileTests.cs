using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Services.Generation;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre la lógica pura y determinista de 7.a-perfil-por-coleccion: resolución de
/// perfil desde el manifiesto/catálogo, overrides de pesos de fusión y selección de
/// familia de prompt. Ninguno de estos casos toca Qdrant ni el modelo ONNX — la
/// cobertura de integración real (dos colecciones con perfiles distintos dando
/// resultados distintos para la MISMA request HTTP, y el caso sin perfil = baseline)
/// vive en <see cref="RetrievalProfileHttpHarnessTests"/>, hermano de este archivo.
/// </summary>
public sealed class RetrievalProfileTests
{
    private static readonly CollectionManifest ManifestSinPerfil = new()
    {
        CollectionName = "col",
        ModelName = "m",
        ModelOnnxSha256 = "h",
        EmbeddingDimension = 384
    };

    private static IRetrievalProfileResolver BuildResolver(Dictionary<string, RetrievalProfile> profiles)
    {
        // El constructor de QdrantVectorStore/QdrantClient no abre conexión de red —
        // solo se ejercita Resolve(CollectionManifest?), la variante pura sin I/O, así
        // que un cliente apuntando a un host que nunca se toca es seguro aquí.
        var store = new QdrantVectorStore(new QdrantClient("localhost", 6334), NullLogger<QdrantVectorStore>.Instance);
        var catalog = Options.Create(new RetrievalProfileCatalogOptions { Profiles = profiles });
        return new RetrievalProfileResolver(store, catalog);
    }

    [Fact]
    public void Resolve_sin_manifiesto_devuelve_null()
    {
        var resolver = BuildResolver(new Dictionary<string, RetrievalProfile>
        {
            ["cualquiera"] = new RetrievalProfile { TopK = 3 }
        });

        Assert.Null(resolver.Resolve(null));
    }

    [Fact]
    public void Resolve_manifiesto_sin_profile_declarado_devuelve_null()
    {
        var resolver = BuildResolver(new Dictionary<string, RetrievalProfile>
        {
            ["cualquiera"] = new RetrievalProfile { TopK = 3 }
        });

        Assert.Null(resolver.Resolve(ManifestSinPerfil));
    }

    [Fact]
    public void Resolve_nombre_de_perfil_ausente_del_catalogo_devuelve_null()
    {
        var resolver = BuildResolver(new Dictionary<string, RetrievalProfile>
        {
            ["otro-perfil"] = new RetrievalProfile { TopK = 3 }
        });
        var manifest = ManifestSinPerfil with { Profile = "no-existe" };

        Assert.Null(resolver.Resolve(manifest));
    }

    [Fact]
    public void Resolve_nombre_declarado_y_presente_en_catalogo_devuelve_el_perfil()
    {
        var perfilEsperado = new RetrievalProfile { TopK = 3, MinScore = 0.2f, UseReRanking = false };
        var resolver = BuildResolver(new Dictionary<string, RetrievalProfile>
        {
            ["code-strict"] = perfilEsperado
        });
        var manifest = ManifestSinPerfil with { Profile = "code-strict" };

        Assert.Same(perfilEsperado, resolver.Resolve(manifest));
    }

    [Fact]
    public void ApplyFusionOverrides_sin_overrides_devuelve_el_baseline_tal_cual()
    {
        var baseline = new RetrievalFusionOptions { WeightCodigo = 1.0, WeightSparse = 1.3, WeightResumen = 2.5, RrfK = 60 };

        var effective = QdrantSemanticRetriever.ApplyFusionOverrides(baseline, null);

        Assert.Same(baseline, effective);
    }

    [Fact]
    public void ApplyFusionOverrides_parcial_solo_pisa_los_campos_declarados()
    {
        var baseline = new RetrievalFusionOptions { WeightCodigo = 1.0, WeightSparse = 1.3, WeightResumen = 2.5, RrfK = 60 };
        var overrides = new RetrievalProfileFusionWeights { WeightResumen = 4.0 };

        var effective = QdrantSemanticRetriever.ApplyFusionOverrides(baseline, overrides);

        Assert.Equal(1.0, effective.WeightCodigo);
        Assert.Equal(1.3, effective.WeightSparse);
        Assert.Equal(4.0, effective.WeightResumen);
        Assert.Equal(60, effective.RrfK);
    }

    [Fact]
    public void ApplyFusionOverrides_completo_pisa_los_cuatro_campos()
    {
        var baseline = new RetrievalFusionOptions { WeightCodigo = 1.0, WeightSparse = 1.3, WeightResumen = 2.5, RrfK = 60 };
        var overrides = new RetrievalProfileFusionWeights
        {
            WeightCodigo = 2.0,
            WeightSparse = 0.5,
            WeightResumen = 3.0,
            RrfK = 30
        };

        var effective = QdrantSemanticRetriever.ApplyFusionOverrides(baseline, overrides);

        Assert.Equal(2.0, effective.WeightCodigo);
        Assert.Equal(0.5, effective.WeightSparse);
        Assert.Equal(3.0, effective.WeightResumen);
        Assert.Equal(30, effective.RrfK);
    }

    [Theory]
    [InlineData(ResponseMode.Technical, null)]
    [InlineData(ResponseMode.Technical, PromptFamily.Auto)]
    public void SelectTemplate_sin_familia_forzada_preserva_la_heuristica_de_contenido(
        ResponseMode responseMode, PromptFamily? forcedFamily)
    {
        var soloDocs = new[] { DocChunk() };
        var soloCodigo = new[] { CodeChunk() };

        var plantillaDocs = SystemPromptComposer.SelectTemplate(soloDocs, responseMode, forcedFamily);
        var plantillaCodigo = SystemPromptComposer.SelectTemplate(soloCodigo, responseMode, forcedFamily);

        Assert.NotEqual(plantillaDocs, plantillaCodigo);
    }

    [Fact]
    public void SelectTemplate_familia_code_forzada_gana_aunque_los_chunks_sean_de_documentacion()
    {
        var soloDocs = new[] { DocChunk() };

        var codeForzado = SystemPromptComposer.SelectTemplate(soloDocs, ResponseMode.Technical, PromptFamily.Code);
        var sinForzar = SystemPromptComposer.SelectTemplate(soloDocs, ResponseMode.Technical, null);

        Assert.NotEqual(sinForzar, codeForzado);
    }

    [Fact]
    public void SelectTemplate_familia_docs_forzada_gana_aunque_los_chunks_sean_de_codigo()
    {
        var soloCodigo = new[] { CodeChunk() };

        var docsForzado = SystemPromptComposer.SelectTemplate(soloCodigo, ResponseMode.Technical, PromptFamily.Docs);
        var sinForzar = SystemPromptComposer.SelectTemplate(soloCodigo, ResponseMode.Technical, null);

        Assert.NotEqual(sinForzar, docsForzado);
    }

    [Fact]
    public void SelectTemplate_modo_simple_ignora_la_familia_forzada()
    {
        var soloCodigo = new[] { CodeChunk() };

        var conFamiliaForzada = SystemPromptComposer.SelectTemplate(soloCodigo, ResponseMode.Simple, PromptFamily.Docs);
        var sinForzar = SystemPromptComposer.SelectTemplate(soloCodigo, ResponseMode.Simple, null);

        Assert.Equal(sinForzar, conFamiliaForzada);
    }

    private static RetrievalResult DocChunk() => new(
        ChunkId: Guid.NewGuid().ToString(),
        Content: "# README\nEsto es documentación en prosa.",
        SimilarityScore: 0.5f,
        ScoreScale: RetrievalScoreScale.RankFusionNative,
        Metadata: new CodeChunkMetadata(
            FilePath: "README.md",
            RelativeFilePath: "README.md",
            Language: SourceLanguage.Markdown,
            Namespace: null,
            ClassName: null,
            MethodName: null,
            StartLine: 1,
            EndLine: 3,
            LastModified: DateTimeOffset.UtcNow,
            RepositoryName: "demo"),
        ContentHash: "hash-doc");

    private static RetrievalResult CodeChunk() => new(
        ChunkId: Guid.NewGuid().ToString(),
        Content: "public sealed class Foo { }",
        SimilarityScore: 0.5f,
        ScoreScale: RetrievalScoreScale.RankFusionNative,
        Metadata: new CodeChunkMetadata(
            FilePath: "Foo.cs",
            RelativeFilePath: "Foo.cs",
            Language: SourceLanguage.CSharp,
            Namespace: "Demo",
            ClassName: "Foo",
            MethodName: null,
            StartLine: 1,
            EndLine: 3,
            LastModified: DateTimeOffset.UtcNow,
            RepositoryName: "demo"),
        ContentHash: "hash-code");
}
