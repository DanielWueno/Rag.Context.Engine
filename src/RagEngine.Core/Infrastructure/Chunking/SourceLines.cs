namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// División de un archivo en líneas, compartida por todas las estrategias de
/// chunking.
///
/// Existe porque no lo estaba: las estrategias de TypeScript y Markdown partían con
/// <c>Split(["\r\n", "\r", "\n"])</c> mientras las de C# y Fallback usaban
/// <c>Split('\n')</c>. En un archivo con finales de línea CRLF, esas dos últimas
/// dejaban un <c>'\r'</c> colgando al final de cada línea, que terminaba dentro del
/// <c>Content</c> del chunk y por tanto dentro de su <c>ContentHash</c>.
///
/// Consecuencia medida (golden master del 2026-08-21, fixture de C# en LF y CRLF con
/// contenido idéntico): 3 de 8 chunks salían con <c>'\r'</c> incrustado y 2 de 8
/// hashes diferían entre las dos versiones del MISMO código. Es decir, el mismo
/// archivo se indexaba distinto según viniera de Windows o de Unix, y un cambio de
/// finales de línea invalidaba entradas de la caché de resúmenes sin que hubiera
/// cambiado una sola línea de código.
/// </summary>
internal static class SourceLines
{
    private static readonly string[] Separadores = ["\r\n", "\r", "\n"];

    /// <summary>
    /// Parte el texto en líneas tratando CRLF, CR y LF por igual, de modo que el
    /// mismo contenido produzca las mismas líneas —y por tanto los mismos hashes—
    /// sin importar de qué sistema venga el archivo.
    /// </summary>
    public static string[] Split(string content) =>
        content.Split(Separadores, StringSplitOptions.None);

    /// <summary>
    /// Normaliza los finales de línea a LF. Se aplica UNA vez, a la entrada del
    /// chunking, antes de parsear: con esto el árbol sintáctico de Roslyn nace en LF
    /// y <c>ToFullString()</c> ya no devuelve CRLF.
    ///
    /// Hace falta además de <see cref="Split"/> porque el chunker de C# no siempre
    /// arma el contenido desde el arreglo de líneas: para constructores, métodos y
    /// grupos de campos lo toma directo del árbol con <c>ToFullString()</c>, que
    /// preserva los finales de línea originales. Unificar solo el Split arreglaba
    /// PlainText y dejaba 3 de 8 chunks de C# con el '\r' incrustado — medido con el
    /// golden master, no supuesto.
    /// </summary>
    public static string Normalize(string content) =>
        content.Contains('\r') ? content.Replace("\r\n", "\n").Replace('\r', '\n') : content;

}
