namespace RagEngine.Core.Domain;

/// <summary>
/// Identifies the programming language of a source artifact.
/// Used by the ChunkingStrategyRouter to select the correct chunking strategy.
/// </summary>
public enum SourceLanguage
{
    CSharp,       // .cs
    TypeScript,   // .ts, .tsx
    JavaScript,   // .js, .jsx
    Xaml,         // .xaml
    Sql,          // .sql
    Markdown,     // .md
    PlainText,    // .txt, .json, .xml, .csproj
    Unknown
}

/// <summary>
/// Distingue los lenguajes que un lector no técnico puede leer sin riesgo de los que no.
/// </summary>
public static class SourceLanguageExtensions
{
    /// <summary>
    /// True si el contenido de un chunk de este lenguaje es prosa escrita para personas.
    /// La redacción del modo Simple existe para que nadie confunda un fragmento de código
    /// crudo con la respuesta; sobre prosa esa protección no aplica y sólo consigue dejar
    /// la fuente en un score sin contexto.
    ///
    /// <see cref="SourceLanguage.PlainText"/> NO cuenta: ese mismo valor cubre .txt pero
    /// también .json, .xml y .csproj, y desde aquí no se pueden distinguir. Ante la duda,
    /// se redacta.
    ///
    /// No confundir con <see cref="IsDocumentation"/>, que difiere sólo en
    /// <see cref="SourceLanguage.PlainText"/> y responde a otra pregunta. Regla para
    /// elegir: si equivocarse **enseña** contenido a un lector no técnico, es
    /// <c>IsProse</c> (conservadora); si sólo cambia una etiqueta o una plantilla, es
    /// <c>IsDocumentation</c>.
    /// </summary>
    public static bool IsProse(this SourceLanguage language) =>
        language == SourceLanguage.Markdown;

    /// <summary>
    /// True si el chunk representa documentación en prosa en vez de código fuente, a
    /// efectos de generación: qué plantilla de sistema se elige y si la cabecera del
    /// chunk se etiqueta "Section" o "Method".
    ///
    /// Es deliberadamente MÁS ancha que <see cref="IsProse"/>: aquí sí entra
    /// <see cref="SourceLanguage.PlainText"/>, porque el coste de equivocarse es
    /// distinto. En redacción, tomar un .json por prosa filtraría código a un lector
    /// no técnico; aquí sólo elegiría la plantilla de documentos para un repositorio
    /// que resultó ser mayormente .txt, que es exactamente lo que se quiere.
    /// </summary>
    public static bool IsDocumentation(this SourceLanguage language) =>
        language is SourceLanguage.Markdown or SourceLanguage.PlainText;
}
