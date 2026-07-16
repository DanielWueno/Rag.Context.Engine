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
