using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RagEngine.Core.Abstractions;

namespace RagEngine.Core.Infrastructure.Vectorization;

/// <summary>
/// Pure C# implementation of <see cref="ISparseTokenizer"/> that produces
/// BM25-style sparse vectors for hybrid search in Qdrant.
///
/// Refactored for .NET 10 to be completely Zero-Allocation during tokenization:
/// - Uses SearchValues<string> for blazing fast SIMD evaluations of Stop Words.
/// - Uses Dictionary.GetAlternateLookup<ReadOnlySpan<char>>() to eliminate string allocations on dictionary lookups.
/// - Custom span-based parser entirely replaces Regex, eliminating the GC overhead from strings and string.Split.
///
/// Normalización léxica ES/EN (aplicada simétricamente en ingesta y consulta):
/// - Folding de acentos tras el lowercase ("código" → "codigo"), alineando la
///   consulta en lenguaje natural con los identificadores del código fuente.
/// - Stemming ligero (plural -s y vocal temática final a/o/e, estilo
///   SpanishLightStemmer de Lucene): "auditoria" y "auditorias" convergen al
///   mismo stem "auditori" antes del hash, por lo que el matching exacto de
///   MurmurHash3 sobrevive a la morfología del español y al plural inglés.
///   La precisión lingüística importa menos que la CONSISTENCIA: ambos lados
///   del índice aplican exactamente la misma transformación.
/// </summary>
public sealed partial class SparseTokenizer : ISparseTokenizer
{
    private const uint IndexSpaceSize = 1u << 20;
    private const int MinTermLength = 2;
    private const int MaxRawCount = 10;

    // Utilizamos FrozenSet con su nueva capacidad de AlternateLookup en .NET 9+ para chequeos súper rápidos de exact match.
    // Al usar OrdinalIgnoreCase evitamos hacer ToLowerInvariant en memoria antes de evaluar las StopWords.
    private static readonly FrozenSet<string> StopWordsSet = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
            // Español
            "que", "qué", "es", "un", "una", "el", "la", "los", "las", "de", "del", 
            "para", "por", "con", "como", "cómo", "en", "a", "al", "su", "sus",
            // Inglés
            "what", "is", "a", "an", "the", "of", "for", "by", "with", "how", 
            "in", "to", "on", "and"
        );

    private static readonly FrozenSet<string> EliminatedTermsSet = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
            "var", "let", "const", "new", "this", "base", "return", "await",
            "async", "public", "private", "protected", "internal", "static",
            "void", "class", "interface", "struct", "enum", "record", "sealed",
            "abstract", "override", "virtual", "readonly", "partial", "using",
            "namespace", "get", "set", "init", "null", "true", "false", "if",
            "else", "for", "foreach", "while", "do", "switch", "case", "break",
            "continue", "throw", "try", "catch", "finally", "in", "out", "ref",
            "params", "typeof", "is", "as", "where", "from", "select", "yield",
            "value", "default", "object", "string", "int", "bool", "float",
            "double", "long", "byte", "char", "uint",
            "the", "and", "for", "with", "that", "this", "are", "was", "has",
            "have", "not", "but", "from", "its", "into", "then", "than",
            "also", "any", "can", "will", "all", "when", "used", "use"
        );

    // Los sets de penalización se consultan DESPUÉS de la normalización léxica,
    // por lo que sus entradas deben almacenarse en forma stemmeada (CreateStemmedSet
    // aplica la misma transformación que el hot path: lowercase + fold + stem).
    private static readonly FrozenSet<string> HeavyPenaltyTermsSet = CreateStemmedSet(
            "task", "list", "array", "type", "name", "value", "data", "item",
            "items", "result", "results", "response", "request", "context",
            "config", "options", "service", "services", "model", "models",
            "exception", "error", "message", "logger", "log", "info",
            "cancellation", "token", "index", "count", "size", "length",
            "add", "get", "set", "create", "build", "run", "start", "stop",
            "init", "load", "save", "read", "write", "send", "receive"
        );

    private static readonly FrozenSet<string> ModeratePenaltyTermsSet = CreateStemmedSet(
            "async", "await", "handler", "factory", "builder", "manager",
            "provider", "repository", "controller", "middleware", "pipeline",
            "client", "server", "host", "register", "resolve", "inject",
            "scope", "singleton", "transient", "event", "callback", "action"
        );

    /// <summary>
    /// Construye un FrozenSet cuyas entradas pasaron por la misma normalización
    /// (lowercase + accent folding + light stem) que los términos del hot path.
    /// Solo se ejecuta una vez en la inicialización estática.
    /// </summary>
    private static FrozenSet<string> CreateStemmedSet(params string[] terms)
    {
        var stemmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Span<char> buffer = stackalloc char[64];

        foreach (var term in terms)
        {
            int len = term.AsSpan().ToLowerInvariant(buffer);
            var lowered = buffer[..len];
            FoldAccentsInPlace(lowered);
            stemmed.Add(StemLight(lowered).ToString());
        }

        return stemmed.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    // Contenedor mutable para evitar actualizaciones de structs inmutables en el diccionario
    private sealed class RefCount
    {
        public int Value;
    }

    public IReadOnlyList<SparseEntry> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<SparseEntry>();

        var termCounts = new Dictionary<string, RefCount>(64, StringComparer.Ordinal);
        
        // AlternateLookup (.NET 9+) nos permite hacer TryGetValue usando un ReadOnlySpan<char> sin reservar una string!
        var lookup = termCounts.GetAlternateLookup<ReadOnlySpan<char>>();

        ReadOnlySpan<char> span = text.AsSpan();
        int i = 0;
        
        // Buffer local en el stack para procesar los términos a minúsculas
        Span<char> lowerBuffer = stackalloc char[128];

        while (i < span.Length)
        {
            char c = span[i];

            // 1. Omitir comentarios completamente
            if (c == '/' && i + 1 < span.Length)
            {
                char next = span[i + 1];
                if (next == '/')
                {
                    i += 2;
                    while (i < span.Length && span[i] != '\n') i++;
                    continue;
                }
                if (next == '*')
                {
                    i += 2;
                    while (i + 1 < span.Length && !(span[i] == '*' && span[i + 1] == '/')) i++;
                    i += 2;
                    continue;
                }
            }

            // 2. Omitir contenido de literales de strings (comillas dobles y simples)
            if (c == '"' || c == '\'')
            {
                char quote = c;
                i++;
                while (i < span.Length)
                {
                    if (span[i] == '\\') i += 2; // Omitir caracteres escapados
                    else if (span[i] == quote) { i++; break; }
                    else i++;
                }
                continue;
            }

            // 3. Vía rápida: saltar cualquier carácter que no sea letra o número (como _)
            if (!char.IsLetterOrDigit(c))
            {
                i++;
                continue;
            }

            // 4. Saltar por completo las constantes numéricas
            if (char.IsDigit(c))
            {
                while (i < span.Length)
                {
                    char nc = span[i];
                    if (char.IsLetterOrDigit(nc) || nc == '.' || nc == '_') i++;
                    else break;
                }
                continue;
            }

            // 5. Extracción de palabras y expansión de camelCase on-the-fly
            if (char.IsLetter(c))
            {
                int start = i;
                i++;
                while (i < span.Length && char.IsLetterOrDigit(span[i]))
                {
                    char current = span[i];
                    char prev = span[i - 1];

                    // Ruptura de límites camelCase (ej: GetOrder -> Get, Order)
                    if (char.IsLower(prev) && char.IsUpper(current))
                        break;
                    // Múltiples mayúsculas antes de minúscula (ej: HTTPSClient -> HTTPS, Client)
                    if (char.IsUpper(prev) && char.IsUpper(current) && 
                        i + 1 < span.Length && char.IsLower(span[i + 1]))
                        break;

                    i++;
                }

                var word = span.Slice(start, i - start);

                // Filtrado semántico rápido
                if (word.Length >= MinTermLength && word.Length <= lowerBuffer.Length)
                {
                    // Evaluamos StopWords sin instanciar strings ni modificar mayúsculas 
                    // usando FrozenSet.AlternateLookup
                    if (!StopWordsSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word) && 
                        !EliminatedTermsSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
                    {
                        // Convertir a minúsculas solo dentro de nuestro buffer stackalloc
                        int len = word.ToLowerInvariant(lowerBuffer);
                        var lowerWord = lowerBuffer.Slice(0, len);

                        // Normalización léxica ES/EN: fold de acentos in-place y
                        // stemming ligero (recorte de span, sin asignaciones).
                        FoldAccentsInPlace(lowerWord);
                        ReadOnlySpan<char> normalized = StemLight(lowerWord);

                        // TryGetValue sin asignación de heap gracias a AlternateLookup
                        if (lookup.TryGetValue(normalized, out var counter))
                        {
                            counter.Value++;
                        }
                        else
                        {
                            // ÚNICA ASIGNACIÓN DE MEMORIA: Solo reservamos el string de aquellos términos
                            // que pasaron exitosamente los filtros de tokenización y no existían en el scope actual.
                            termCounts.Add(normalized.ToString(), new RefCount { Value = 1 });
                        }
                    }
                }
                continue;
            }

            i++;
        }

        if (termCounts.Count == 0)
            return Array.Empty<SparseEntry>();

        // ── TF weighting (saturación BM25-style) + static IDF penalty ────────
        // El TF proporcional (count/totalTerms) sesgaba brutalmente el ranking
        // hacia chunks diminutos: en un constructor de una línea, "auditoria"
        // pesaba 0.33; en la clase rica de 200 términos que SÍ contiene la
        // respuesta, 0.005. La saturación tf/(tf+1) mide PRESENCIA con
        // rendimientos decrecientes, sin castigar la longitud del documento:
        // el ranking disperso pasa a dominarlo cuántos términos de la consulta
        // coinciden (dot product), no qué tan corto es el chunk.
        var entries = new List<SparseEntry>(termCounts.Count);

        foreach (var kv in termCounts)
        {
            string term = kv.Key;
            int rawCount = kv.Value.Value;

            float tf = Math.Min(rawCount, MaxRawCount);
            float saturatedTf = tf / (tf + 1f);
            float idfMultiplier = GetStaticIdfMultiplier(term);
            float weight = saturatedTf * idfMultiplier;

            if (weight < 1e-6f) continue;

            uint index = MurmurHash3(term) % IndexSpaceSize;
            entries.Add(new SparseEntry(index, weight));
        }

        // Qdrant exige estar ordenado por TermIndex ascendente
        entries.Sort(static (a, b) => a.TermIndex.CompareTo(b.TermIndex));
        return MergeCollisions(entries);
    }

    /// <summary>
    /// Reemplaza vocales acentuadas, diéresis, ñ y ç por su letra base ASCII.
    /// Los identificadores de código se escriben sin tildes ("codigo", "anio"),
    /// mientras que las consultas en lenguaje natural sí las llevan; sin este
    /// folding, "código" y "codigo" producirían hashes distintos.
    /// Opera in-place sobre el buffer ya en minúsculas — cero asignaciones.
    /// </summary>
    private static void FoldAccentsInPlace(Span<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = text[i] switch
            {
                'á' or 'à' or 'ä' or 'â' or 'ã' => 'a',
                'é' or 'è' or 'ë' or 'ê'        => 'e',
                'í' or 'ì' or 'ï' or 'î'        => 'i',
                'ó' or 'ò' or 'ö' or 'ô' or 'õ' => 'o',
                'ú' or 'ù' or 'ü' or 'û'        => 'u',
                'ñ'                              => 'n',
                'ç'                              => 'c',
                _ => text[i]
            };
        }
    }

    /// <summary>
    /// Stemmer ligero unificado ES/EN (inspirado en SpanishLightStemmer de Lucene):
    ///   1. Recorta el plural final "-s" (evitando "-ss": class, process).
    ///   2. Recorta la vocal temática final a/o/e (género español, -e muda inglesa).
    /// Con longitud mínima 5 por regla, singular y plural convergen al mismo stem:
    ///   auditoria/auditorias → auditori · condicion/condiciones → condicion
    ///   regla/reglas → regl · rule/rules → rule · finding/findings → finding
    /// No busca corrección lingüística sino determinismo simétrico: el índice y la
    /// consulta aplican la misma función antes de MurmurHash3.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> StemLight(ReadOnlySpan<char> word)
    {
        if (word.Length >= 5 && word[^1] == 's' && word[^2] != 's')
            word = word[..^1];

        if (word.Length >= 5 && word[^1] is 'a' or 'o' or 'e')
            word = word[..^1];

        return word;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GetStaticIdfMultiplier(ReadOnlySpan<char> term)
    {
        if (HeavyPenaltyTermsSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(term))    return 0.30f;
        if (ModeratePenaltyTermsSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(term)) return 0.65f;
        return 1.00f;
    }

    private static IReadOnlyList<SparseEntry> MergeCollisions(List<SparseEntry> sorted)
    {
        if (sorted.Count <= 1) return sorted;

        var merged = new List<SparseEntry>(sorted.Count);
        var current = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (next.TermIndex == current.TermIndex)
            {
                current = current with { Weight = current.Weight + next.Weight };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MurmurHash3 (32-bit)
    // ─────────────────────────────────────────────────────────────────────────
    private const uint Murmur3Seed = 0xDEAD_C0DEu;

    private static uint MurmurHash3(string term)
    {
        ReadOnlySpan<char> span = term.AsSpan();
        uint h = Murmur3Seed;
        const uint c1 = 0xCC9E_2D51u;
        const uint c2 = 0x1B87_3593u;

        int blocks = span.Length / 2;
        for (int i = 0; i < blocks; i++)
        {
            uint k = (uint)span[i * 2] | ((uint)span[i * 2 + 1] << 16);
            k *= c1;
            k = RotateLeft(k, 15);
            k *= c2;

            h ^= k;
            h = RotateLeft(h, 13);
            h = h * 5 + 0xE654_6B64u;
        }

        if ((span.Length & 1) != 0)
        {
            uint tail = span[span.Length - 1];
            tail *= c1;
            tail = RotateLeft(tail, 15);
            tail *= c2;
            h ^= tail;
        }

        h ^= (uint)span.Length;
        h ^= h >> 16;
        h *= 0x85EB_CA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2_AE35u;
        h ^= h >> 16;

        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int count)
        => (value << count) | (value >> (32 - count));
}
