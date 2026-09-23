using System.Runtime.InteropServices;
using RagEngine.Core.Utilities;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Portabilidad x64 del binario ONNX: en arm64 se sigue usando el binario int8
/// optimizado (comportamiento histórico, sin cambios, para no reintroducir el
/// exit 134 documentado en el ledger); en cualquier otra arquitectura cae al
/// binario genérico fp32 que <c>infra/download-model.sh</c> ya descarga siempre.
/// </summary>
public class RagEnginePathsTests
{
    private const string ArmPath =
        "/home/user/models/paraphrase-multilingual-MiniLM-L12-v2/model_qint8_arm64.onnx";

    private const string GenericPath =
        "/home/user/models/paraphrase-multilingual-MiniLM-L12-v2/model.onnx";

    [Fact]
    public void SelectArchitectureBinary_EnArm64_ConservaElSufijo()
    {
        string result = RagEnginePaths.SelectArchitectureBinary(ArmPath, Architecture.Arm64);

        Assert.Equal(ArmPath, result);
    }

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void SelectArchitectureBinary_FueraDeArm64_CaeAlBinarioGenerico(Architecture architecture)
    {
        string result = RagEnginePaths.SelectArchitectureBinary(ArmPath, architecture);

        Assert.Equal(GenericPath, result);
    }

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X64)]
    public void SelectArchitectureBinary_SinSufijoArm64_NoSeToca(Architecture architecture)
    {
        const string path = "/home/user/models/reranker/model.onnx";

        string result = RagEnginePaths.SelectArchitectureBinary(path, architecture);

        Assert.Equal(path, result);
    }

    [Fact]
    public void ResolveModelPath_ConRutaAbsolutaConSufijo_AplicaSeleccionDeArquitectura()
    {
        // El caso real: appsettings.json trae "${RAG_MODELS_DIR}/.../model_qint8_arm64.onnx".
        // Tras expandir la variable, la ruta queda absoluta (rooted) — este test
        // cubre justo esa rama, que es la que usa el ModelPath por defecto.
        string result = RagEnginePaths.ResolveModelPath(ArmPath);

        string expected = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ArmPath
            : GenericPath;

        Assert.Equal(expected, result);
    }
}
