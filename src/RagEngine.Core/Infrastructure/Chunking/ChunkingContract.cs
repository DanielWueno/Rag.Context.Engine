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
///   1 — estado al 2026-08-21: 4 estrategias con algoritmos duplicados, split de
///       líneas inconsistente entre ellas (ver ítem 2.1 del plan) y el chunker de
///       TypeScript separando el cuerpo de su firma en estilos Allman y multilínea
///       (ítem 2.0, confirmado con test).
/// </summary>
public static class ChunkingContract
{
    public const int Version = 1;
}
