namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 19.6 — nombres de categoría xUnit (<c>[Trait("Category", ...)]</c>) que declaran,
/// por MÉTODO o por CLASE, una dependencia real de infraestructura externa viva que el
/// runner de CI no tiene por defecto (Qdrant, modelos ONNX descargados en disco, o un
/// servidor Ollama real). El criterio para decidir si un test necesita una de estas
/// categorías NO es que el archivo mencione el tipo (p. ej. construir un
/// <c>QdrantClient</c> para un fake/fixture en memoria no cuenta), sino que el test deje
/// de pasar sin esa infraestructura viva — verificado contra la corrida de CI real
/// 35894801007 (68 de sus 73 fallos son exactamente esto) y, para los casos no cubiertos
/// por esa corrida, leyendo el código del test.
///
/// Por qué un Trait y no el nombre de la clase: el filtro de <c>dotnet test</c>
/// (<c>--filter "Category!=RequiereQdrant&amp;Category!=RequiereOnnx&amp;Category!=RequiereOllama"</c>)
/// no necesita mantener una lista de nombres de clase en <c>ci.yml</c> — un test nuevo
/// que declare su Trait correctamente queda cubierto sin tocar el workflow.
///
/// Un test puede declarar más de una categoría (p. ej. un harness HTTP que toca Qdrant Y
/// vectoriza con el brain ONNX real) repitiendo el atributo <c>[Trait]</c> — xUnit no
/// limita cuántos pares Category/valor lleva un mismo método o clase.
/// </summary>
internal static class TestCategories
{
    /// <summary>Requiere un Qdrant real alcanzable en localhost:6334 (gRPC).</summary>
    internal const string RequiereQdrant = "RequiereQdrant";

    /// <summary>Requiere el/los modelo(s) ONNX reales descargados en disco (infra/download-model.sh).</summary>
    internal const string RequiereOnnx = "RequiereOnnx";

    /// <summary>Requiere un servidor Ollama (u OpenAI-compatible) real alcanzable.</summary>
    internal const string RequiereOllama = "RequiereOllama";
}
