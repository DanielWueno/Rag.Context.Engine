namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Versión declarada del contrato de chunking. No la deduce nadie: la sube a mano
/// quien cambia CÓMO se parte el código, porque no existe forma automática de
/// saber que dos corridas usaron el mismo criterio de corte.
///
/// Para qué sirve: un número de recall@k solo es comparable con otro si los chunks
/// que hay en el índice se generaron igual. Cambiar los límites de token, el
/// solapamiento, el encabezado semántico, el criterio de agrupación
/// o el filtro de admisión a la ingesta invalida
/// cualquier comparación con un baseline anterior — y hoy eso se descubría
/// discutiendo, no mirando un campo.
///
/// SUBIR ESTE NÚMERO cuando cambie el comportamiento observable de cualquier
/// IChunkingStrategy o de la selección de chunks que se indexan. No hace falta
/// subirlo por un refactor que no mueve ni un byte ni cambia qué chunks se
/// conservan; sí hace falta si cambia cualquiera de los dos.
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
///   3 — 2026-09-12: la ingesta permite conservar declaraciones cortas de tipos con
///       IndexShortTypeDeclarations (opt-in, false por defecto), identificadas por
///       DefinedSymbols y ClassName, sin eximir grupos de campos ni otros micro-chunks.
///       Incluye la normalización explícita del espacio antes de ':'
///       en declaraciones C# introducida en 5.g. Requiere re-ingesta y nueva línea
///       base; los índices existentes no se migran al cambiar esta constante.
/// </summary>
public static class ChunkingContract
{
    public const int Version = 3;
}
