using RagEngine.Core.Utilities;

namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Agrupa párrafos en lotes que quepan dentro del presupuesto de tokens de un
/// chunk, contando el encabezado en cada lote.
///
/// Existe porque el mismo algoritmo estaba escrito dos veces —en la estrategia de
/// TypeScript y en la de Markdown— con la misma sutileza en el orden: se prueba si
/// el párrafo entrante cabe, y si no cabe se emite lo acumulado ANTES de agregarlo,
/// nunca después. Un fix en esa lógica había que aplicarlo en dos sitios.
///
/// Devuelve rangos de ÍNDICE de párrafo en vez de números de línea a propósito: los
/// dos llamadores derivan sus líneas de forma distinta —Markdown hace que el primer
/// lote arranque en la línea de la cabecera de sección, y TypeScript acumula
/// longitudes de párrafo— y unificar eso habría cambiado la metadata de los chunks.
/// Lo que se comparte es la decisión de presupuesto, no la contabilidad de líneas.
/// </summary>
internal static class ParagraphBudget
{
    /// <summary>
    /// Recorre los párrafos y emite lotes con el rango de índices que abarca cada
    /// uno. El encabezado se incluye en la estimación de cada lote porque viaja
    /// dentro del chunk y consume presupuesto real.
    /// </summary>
    public static IEnumerable<(List<string> Textos, int PrimerIndice, int UltimoIndice)> Agrupar(
        IReadOnlyList<string> parrafos,
        string header,
        int maxTokens)
    {
        var acumulado = new List<string>();
        int primero = 0;

        for (int i = 0; i < parrafos.Count; i++)
        {
            var prueba = $"{header}\n\n{string.Join("\n\n", acumulado.Append(parrafos[i]))}";

            if (TokenEstimator.Estimate(prueba) > maxTokens && acumulado.Count > 0)
            {
                yield return (acumulado, primero, i - 1);
                acumulado = [];
                primero = i;
            }

            acumulado.Add(parrafos[i]);
        }

        if (acumulado.Count > 0)
        {
            yield return (acumulado, primero, parrafos.Count - 1);
        }
    }
}
