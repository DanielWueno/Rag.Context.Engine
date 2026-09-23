using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Chunking;
using RagEngine.Core.Infrastructure.Vectorization;
using RagEngine.Core.Utilities;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 11.1 — instrumenta tokens reales/descartados/truncados con el
/// tokenizador EFECTIVO (no una aproximación), sobre las dos familias que
/// soporta <see cref="OnnxVectorizationBrain"/>: WordPiece (all-MiniLM-L6-v2)
/// y SentencePiece (paraphrase-multilingual-MiniLM-L12-v2).
///
/// M=MaxSequenceLength se fija deliberadamente pequeño (10) para poder
/// construir fixtures L-1/L/L+1 con pocos tokens; S (especiales) se verifica
/// en tiempo de ejecución contra IDs reales sin truncar — no se asume por el
/// nombre de la familia (S=2 en ambas, pero por razones DISTINTAS: BertTokenizer
/// añade [CLS]/[SEP] él mismo; el wrapper SentencePiece de este código añade
/// &lt;s&gt;/&lt;/s&gt; manualmente).
///
/// Los fixtures de C#/TS/prosa/encabezado se ajustan a un T exacto AJUSTANDO
/// (recortando o rellenando con un filler de 1 token, verificado empíricamente)
/// contra el propio tokenizador real — nunca se asume cuántos tokens produce un
/// texto de memoria.
///
/// Si los modelos no están descargados en este entorno, los tests se saltan
/// explícitamente (mismo patrón que <c>CrossEncoderStableGateScoreTests</c>),
/// no fallan en rojo por ausencia de infraestructura (ítem 1.7).
/// </summary>
public class OnnxVectorizationBrainTokenizationTests
{
    private const int TestMaxSequenceLength = 10;

    private const string WordPieceModelRelPath = "models/all-MiniLM-L6-v2/model.onnx";
    private const string WordPieceVocabRelPath = "models/all-MiniLM-L6-v2/vocab.txt";

    private const string SentencePieceModelRelPath =
        "models/paraphrase-multilingual-MiniLM-L12-v2/model_qint8_arm64.onnx";
    private const string SentencePieceVocabRelPath =
        "models/paraphrase-multilingual-MiniLM-L12-v2/sentencepiece.bpe.model";

    // ── Contenido temático real por tipo de chunk (11.1: "Validar C#/TS/prosa
    // y prefijo enriquecido con el tokenizador real") ──────────────────────
    private const string CuerpoCSharp =
        "public sealed class InvoiceProcessor private readonly IRepository repository " +
        "public async Task ProcessAsync int invoiceId var invoice await repository GetAsync " +
        "invoiceId if invoice is null throw new InvalidOperationException invoice not found " +
        "invoice MarkAsPaid await repository SaveChangesAsync invoice";

    private const string CuerpoTypeScript =
        "export class InvoiceProcessor constructor private readonly repository Repository " +
        "async process invoiceId number Promise void const invoice await this repository get " +
        "invoiceId if not invoice throw new Error invoice not found invoice markAsPaid " +
        "await this repository save invoice";

    private const string CuerpoProsa =
        "Para cerrar una factura el sistema primero valida que el cliente no tenga saldo " +
        "pendiente y luego marca la factura como pagada antes de notificar al area contable " +
        "sobre el cambio de estado registrado en el historial de auditoria";

    private static string CuerpoConEncabezado(string cuerpo) =>
        ChunkBuilder.HeaderPrefix("billing-service", "src/Billing/InvoiceProcessor.cs") + "\n" + cuerpo;

    // ── Helpers de tokenizador real, independientes del brain, para construir
    // fixtures de T exacto (L-1, L, L+1) ANTES de instanciar el brain ────────

    private static Func<string, int> ContadorSinEspeciales(Tokenizer tokenizer, bool esSentencePiece) =>
        text => esSentencePiece
            ? tokenizer.EncodeToIds(text).Count
            : ((BertTokenizer)tokenizer).EncodeToIds(text, addSpecialTokens: false).Count;

    private static string DescubrirFillerDeUnToken(Func<string, int> contar)
    {
        string[] candidatos = ["a", "x", "el", "de", "1", "test"];
        foreach (var candidato in candidatos)
            if (contar(candidato) == 1) return candidato;

        throw new InvalidOperationException(
            "Ningún candidato de relleno tokeniza a exactamente 1 token con este tokenizador real; "
          + "el fixture de 11.1 no puede ajustarse sin uno.");
    }

    /// <summary>
    /// Ajusta <paramref name="baseText"/> a EXACTAMENTE <paramref name="target"/>
    /// tokens reales (recortando palabra por palabra o rellenando con un filler
    /// de 1 token), verificando el resultado contra el propio tokenizador —
    /// nunca contra una estimación.
    /// </summary>
    private static string ConstruirFixtureDeTExacto(Func<string, int> contar, string baseText, int target)
    {
        string filler = DescubrirFillerDeUnToken(contar);

        var palabras = baseText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string texto = string.Join(' ', palabras);
        int conteo = contar(texto);

        while (conteo > target && palabras.Count > 0)
        {
            palabras.RemoveAt(palabras.Count - 1);
            texto = string.Join(' ', palabras);
            conteo = contar(texto);
        }

        while (conteo < target)
        {
            texto = string.IsNullOrEmpty(texto) ? filler : texto + " " + filler;
            conteo = contar(texto);
        }

        if (conteo != target)
        {
            throw new InvalidOperationException(
                $"No se pudo ajustar el fixture a T={target} tokens reales (quedó en {conteo}). "
              + "El filler de 1 token no fue aditivo puro para este tokenizador.");
        }

        return texto;
    }

    private static OnnxBrainOptions ConstruirOpciones(
        OnnxTokenizerKind kind, string modelRelPath, string vocabRelPath) => new()
    {
        ModelPath = RagEnginePaths.ResolveModelPath(modelRelPath),
        VocabPath = RagEnginePaths.ResolveModelPath(vocabRelPath),
        TokenizerType = kind,
        MaxSequenceLength = TestMaxSequenceLength,
        BatchSize = 8,
        EmbeddingDimensions = 384
    };

    private static OnnxVectorizationBrain ConstruirBrain(OnnxBrainOptions options) =>
        new(Options.Create(options), NullLogger<OnnxVectorizationBrain>.Instance);

    /// <summary>
    /// Núcleo del oráculo de 11.1: para T=[L-1,L,L+1], descartados=[0,0,1] y
    /// truncado=[false,false,true], con L=M-S verificado contra el tokenizador
    /// real (no asumido). Cubre C#, TypeScript, prosa y prefijo enriquecido en
    /// ambas familias de tokenizador.
    /// </summary>
    public static IEnumerable<object[]> CasosDeContenido()
    {
        yield return ["csharp", CuerpoCSharp];
        yield return ["typescript", CuerpoTypeScript];
        yield return ["prosa", CuerpoProsa];
        yield return ["encabezado+csharp", CuerpoConEncabezado(CuerpoCSharp)];
    }

    [Theory]
    [MemberData(nameof(CasosDeContenido))]
    public async Task WordPiece_TripletaLMenosUnoLYLMasUno_DaDescartadosYTruncadoExactos(
        string _, string cuerpoBase)
    {
        if (!ModeloWordPieceDisponible(out var options, out var motivoSkip))
        {
            return; // ver ModeloWordPieceDisponible: sin infra/download-model.sh en este entorno
        }

        var tokenizerReal = BertTokenizer.Create(options.VocabPath, new BertOptions
        {
            LowerCaseBeforeTokenization = true
        });
        var contar = ContadorSinEspeciales(tokenizerReal, esSentencePiece: false);

        // S=2 ([CLS]/[SEP]) verificado contra IDs reales, no asumido.
        int conEspeciales = tokenizerReal.EncodeToIds(string.Empty, addSpecialTokens: true).Count;
        int sinEspeciales = tokenizerReal.EncodeToIds(string.Empty, addSpecialTokens: false).Count;
        int s = conEspeciales - sinEspeciales;
        int l = TestMaxSequenceLength - s;

        using var brain = ConstruirBrain(options);

        await VerificarTripletaAsync(brain, contar, cuerpoBase, l);
    }

    [Theory]
    [MemberData(nameof(CasosDeContenido))]
    public async Task SentencePiece_TripletaLMenosUnoLYLMasUno_DaDescartadosYTruncadoExactos(
        string _, string cuerpoBase)
    {
        if (!ModeloSentencePieceDisponible(out var options, out var motivoSkip))
        {
            return; // ver ModeloSentencePieceDisponible: sin infra/download-model.sh en este entorno
        }

        using var spmStream = File.OpenRead(options.VocabPath);
        var tokenizerReal = SentencePieceTokenizer.Create(
            spmStream, addBeginningOfSentence: false, addEndOfSentence: false);
        var contar = ContadorSinEspeciales(tokenizerReal, esSentencePiece: true);

        // S=2 (<s>/</s>) es una constante de ESTE código (EncodeSentencePiece
        // los añade manualmente); no depende del tokenizer subyacente, así que
        // aquí se documenta explícitamente en vez de "descubrirse".
        const int s = 2;
        int l = TestMaxSequenceLength - s;

        using var brain = ConstruirBrain(options);

        await VerificarTripletaAsync(brain, contar, cuerpoBase, l);
    }

    private static async Task VerificarTripletaAsync(
        OnnxVectorizationBrain brain, Func<string, int> contar, string cuerpoBase, int l)
    {
        string textoLMenos1 = ConstruirFixtureDeTExacto(contar, cuerpoBase, l - 1);
        string textoL       = ConstruirFixtureDeTExacto(contar, cuerpoBase, l);
        string textoLMas1   = ConstruirFixtureDeTExacto(contar, cuerpoBase, l + 1);

        var resultado = await brain.GenerateBatchEmbeddingsWithStatsAsync(
            [textoLMenos1, textoL, textoLMas1]);

        Assert.Equal(3, resultado.Stats.Count);

        var statsLMenos1 = resultado.Stats[0];
        var statsL       = resultado.Stats[1];
        var statsLMas1   = resultado.Stats[2];

        // T observado debe coincidir con el fixture construido.
        Assert.Equal(l - 1, statsLMenos1.TotalTokens);
        Assert.Equal(l,     statsL.TotalTokens);
        Assert.Equal(l + 1, statsLMas1.TotalTokens);

        // L (MaxUsableTokens) es el mismo para los tres: mismo tokenizer/M.
        Assert.Equal(l, statsLMenos1.MaxUsableTokens);
        Assert.Equal(l, statsL.MaxUsableTokens);
        Assert.Equal(l, statsLMas1.MaxUsableTokens);

        // Oráculo: descartados=[0,0,1], truncado=[false,false,true].
        Assert.Equal(0, statsLMenos1.Discarded);
        Assert.Equal(0, statsL.Discarded);
        Assert.Equal(1, statsLMas1.Discarded);

        Assert.False(statsLMenos1.Truncated);
        Assert.False(statsL.Truncated);
        Assert.True(statsLMas1.Truncated);

        // Las tres embeddings se produjeron igual (la medición no rompe la
        // ruta de embedding real: mismo batch, misma dimensión de salida).
        Assert.Equal(3, resultado.Embeddings.Count);
        foreach (var embedding in resultado.Embeddings)
            Assert.Equal(brain.EmbeddingDimensions, embedding.Length);
    }

    private static bool ModeloWordPieceDisponible(out OnnxBrainOptions options, out string? motivo)
    {
        options = ConstruirOpciones(OnnxTokenizerKind.WordPiece, WordPieceModelRelPath, WordPieceVocabRelPath);
        if (!File.Exists(options.ModelPath) || !File.Exists(options.VocabPath))
        {
            motivo = $"Modelo/tokenizer WordPiece no encontrado en '{options.ModelPath}'. "
                   + "Corre 'bash infra/download-model.sh english' para habilitar este test.";
            return false;
        }

        motivo = null;
        return true;
    }

    private static bool ModeloSentencePieceDisponible(out OnnxBrainOptions options, out string? motivo)
    {
        options = ConstruirOpciones(
            OnnxTokenizerKind.SentencePiece, SentencePieceModelRelPath, SentencePieceVocabRelPath);
        if (!File.Exists(options.ModelPath) || !File.Exists(options.VocabPath))
        {
            motivo = $"Modelo/tokenizer SentencePiece no encontrado en '{options.ModelPath}'. "
                   + "Corre 'bash infra/download-model.sh multilingual' para habilitar este test.";
            return false;
        }

        motivo = null;
        return true;
    }
}
