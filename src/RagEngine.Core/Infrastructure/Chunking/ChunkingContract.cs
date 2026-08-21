namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Versión declarada del contrato de chunking. No la deduce nadie: la sube a mano
/// quien cambia CÓMO se parte el código, porque no existe forma automática de
/// saber que dos corridas usaron el mismo criterio de corte.
///
/// Para qué sirve: un número de recall@k solo es comparable con otro si los chunks
/// que hay en el índice se generaron igual. Cambiar los límites de token, el
/// solapamiento, el encabezado semántico o el criterio de agrupación invalida
/// cualquier comparación con un baseline anterior — y hoy eso se descubría
/// discutiendo, no mirando un campo.
///
/// SUBIR ESTE NÚMERO cuando cambie el comportamiento observable de cualquier
/// IChunkingStrategy. No hace falta subirlo por un refactor que no mueve ni un
/// byte de los chunks producidos; sí hace falta si mueve alguno.
///
/// Historial:
///   1 — estado al 2026-08-21 por la mañana: 4 estrategias con algoritmos
///       duplicados, split de líneas inconsistente entre ellas y el chunker de
///       TypeScript separando el cuerpo de su firma en estilos Allman y multilínea.
///   2 — 2026-08-21: finales de línea normalizados a LF en un único punto, antes de
///       parsear, así que un archivo CRLF produce ahora los mismos chunks y los
///       mismos hashes que su equivalente LF; y el chunker de TypeScript mantiene el
///       cuerpo junto a su firma en los tres estilos de llave. La construcción de
///       CodeChunk y la geometría de la ventana se extrajeron a ChunkBuilder, pero
///       eso NO movió ningún byte (verificado con el golden master) y por sí solo no
///       habría justificado subir la versión.
///
///       Medido antes de subirla: para los corpus ingestados hoy el cambio es un
///       no-op. BusinessSuite.Xaf son 2.097 archivos con CERO CRLF y CERO .ts, y las
///       demás fuentes tienen un único archivo CRLF en total. La versión sube igual
///       porque describe el comportamiento del CHUNKER, no el de un corpus concreto:
///       el mismo código sobre un corpus con CRLF o con TypeScript sí produce chunks
///       distintos que en la versión 1.
/// </summary>
public static class ChunkingContract
{
    public const int Version = 2;
}
