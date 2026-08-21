using Microsoft.ML.OnnxRuntime;

namespace RagEngine.Core.Infrastructure.Vectorization;

/// <summary>
/// Cierra el runtime nativo de ONNX antes de que termine el proceso.
///
/// El problema: ONNX Runtime mantiene un entorno global (<see cref="OrtEnv"/>) por
/// proceso, independiente de las <c>InferenceSession</c>. Aunque las sesiones se
/// liberen correctamente, ese entorno se destruye durante el apagado del proceso,
/// cuando el CLR ya cerró sus hilos, y en Apple Silicon eso aborta con
/// <c>libc++abi: terminating due to uncaught exception ... mutex lock failed:
/// Invalid argument</c> — SIGABRT, exit 134, DESPUÉS de que el comando ya imprimió
/// su resultado correctamente.
///
/// Medido en este repo el 2026-08-21: 14 de 14 corridas terminaban en 134
/// (10 de <c>rag doctor</c> y 4 de <c>rag search --rerank</c>); liberando el
/// entorno explícitamente, 0 de 28. Es determinista, no una carrera — razón por la
/// cual el <c>Task.Delay(300)</c> que antes intentaba mitigarlo nunca funcionó: el
/// fallo ocurría igual el 100% de las veces con el delay puesto.
///
/// Por qué importa más allá de la estética: el comando funcionaba, pero devolvía un
/// código de salida de fallo. Cualquier script, CI o cron que mire <c>$?</c> lo
/// habría leído como error.
/// </summary>
public static class OnnxRuntimeLifetime
{
    private static volatile bool _runtimeTouched;

    /// <summary>
    /// Registra que alguien creó (o intentó crear) una InferenceSession, y por tanto
    /// que el entorno global existe y hay que liberarlo al salir. Se lleva la cuenta
    /// a mano porque <c>OrtEnv</c> 1.21 no expone si ya fue creado, y llamar a
    /// <c>Instance()</c> para averiguarlo lo crearía — inicializando un runtime
    /// nativo en comandos que no lo necesitan.
    /// </summary>
    public static void MarkRuntimeTouched() => _runtimeTouched = true;

    /// <summary>
    /// Libera el entorno global de ONNX si se usó. Debe llamarse UNA sola vez, al
    /// final del proceso y <b>después</b> de que todas las sesiones se hayan
    /// liberado (en la práctica: después de destruir el contenedor de DI, que es
    /// quien posee los singletons que las contienen).
    ///
    /// No propaga excepciones: corre en el camino de apagado, donde lanzar
    /// enmascararía el código de salida real del comando.
    /// </summary>
    public static void Shutdown()
    {
        if (!_runtimeTouched)
        {
            return;
        }

        try
        {
            OrtEnv.Instance().Dispose();
        }
        catch
        {
            // Si el entorno ya no está disponible, no hay nada que liberar y no hay
            // nada útil que reportar: el proceso está terminando.
        }
        finally
        {
            _runtimeTouched = false;
        }
    }
}
