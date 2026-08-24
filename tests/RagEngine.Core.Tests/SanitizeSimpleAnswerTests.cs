using RagEngine.Core.Services.Generation;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// El sanitizador del modo Simple es la única garantía ESTRUCTURAL de que no se
/// filtre código a un lector no técnico: son seis regex sobre texto libre de un
/// LLM, así que hasta ahora un backtick sin cerrar o un identificador con acentos
/// solo se detectaba corriendo el chat a mano.
///
/// Las aserciones son sobre PROPIEDADES (el código desaparece; la prosa
/// sobrevive), no sobre los literales de los avisos: si mañana se reescribe el
/// texto del placeholder, estos tests deben seguir siendo válidos.
///
/// Cada bloque incluye negativos adversariales. Un sanitizador que borra prosa
/// legítima es tan defectuoso como uno que filtra código, y solo los negativos
/// detectan ese fallo.
/// </summary>
public class SanitizeSimpleAnswerTests
{
    // ── Bloques de código cercados ────────────────────────────────────────────

    [Fact]
    public void BloqueCercadoConCodigo_DesapareceElCodigo()
    {
        var entrada = "Para cancelar:\n```csharp\nvar x = repo.GetById(id);\n```\nY listo.";

        var salida = SimpleAnswerSanitizer.Sanitize(entrada);

        Assert.DoesNotContain("repo.GetById", salida);
        Assert.DoesNotContain("var x", salida);
        Assert.DoesNotContain("```", salida);
        Assert.Contains("Para cancelar", salida);
        Assert.Contains("Y listo", salida);
    }

    [Fact]
    public void BloqueCercadoVacio_SeEliminaSinDejarAviso()
    {
        var entrada = "Antes\n```\n\n```\nDespués";

        var salida = SimpleAnswerSanitizer.Sanitize(entrada);

        Assert.DoesNotContain("```", salida);
        Assert.DoesNotContain("omitió", salida);
        Assert.Contains("Antes", salida);
        Assert.Contains("Después", salida);
    }

    [Fact]
    public void BackticksSinCerrar_NoDejaPasarElCodigoQueLosSigue()
    {
        // Caso adversarial: el LLM abre un bloque y no lo cierra. El patrón de
        // bloque cercado no matchea, así que la red de seguridad tiene que ser el
        // paso de identificadores en texto plano.
        var entrada = "Mira esto:\n```csharp\nservicio.GuardarOrden(orden);";

        var salida = SimpleAnswerSanitizer.Sanitize(entrada);

        Assert.DoesNotContain("GuardarOrden", salida);
    }

    // ── Spans de código en línea ──────────────────────────────────────────────

    [Theory]
    [InlineData("El campo `IsAuditado` marca el estado.", "IsAuditado")]
    [InlineData("Se usa `tipo_estatus` internamente.", "tipo_estatus")]
    [InlineData("La clase `ServicioCliente` lo resuelve.", "ServicioCliente")]
    public void SpanEnLinea_ConFormaDeIdentificador_SeHumaniza(string entrada, string identificador)
    {
        var salida = SimpleAnswerSanitizer.Sanitize(entrada);

        Assert.DoesNotContain(identificador, salida);
        Assert.DoesNotContain("`", salida);
    }

    [Fact]
    public void SpanEnLinea_QueNoEsIdentificador_SeReemplazaPorAviso()
    {
        var entrada = "Ejecuta `SELECT * FROM tabla` para verlo.";

        var salida = SimpleAnswerSanitizer.Sanitize(entrada);

        Assert.DoesNotContain("SELECT", salida);
        Assert.DoesNotContain("`", salida);
    }

    // ── Humanización de identificadores ───────────────────────────────────────

    [Theory]
    [InlineData("GenerarPlanAuditoria", "generar plan auditoria")]
    [InlineData("ServicioCliente", "servicio cliente")]
    [InlineData("TipoEstatus.Completado", "tipo estatus completado")]
    [InlineData("fecha_de_cierre", "fecha de cierre")]
    public void Identificador_SeConvierteEnPalabrasLegibles(string identificador, string esperado)
    {
        var salida = SimpleAnswerSanitizer.Sanitize($"El valor {identificador} importa.");

        Assert.Contains(esperado, salida);
        Assert.DoesNotContain(identificador, salida);
    }

    // ── Negativos adversariales: la prosa NO se toca ──────────────────────────

    [Theory]
    [InlineData("La Auditoría se cierra el viernes.")]
    [InlineData("El usuario que reportó la petición puede cancelarla.")]
    [InlineData("Solo Ventas y Compras aprueban la orden.")]
    [InlineData("El estatus pasa a Registrado cuando se guarda.")]
    public void ProsaConNombresPropios_SobreviveIntacta(string prosa)
    {
        // Una sola palabra capitalizada es un nombre propio legítimo, no un
        // identificador. Que el sanitizador sea conservador aquí es DELIBERADO:
        // ver el comentario de CamelHumpIdentifierPattern.
        var salida = SimpleAnswerSanitizer.Sanitize(prosa);

        Assert.Equal(prosa, salida);
    }

    [Fact]
    public void ProsaConAcentosYPuntuacion_SobreviveIntacta()
    {
        var prosa = "¿Quién puede cancelar? Únicamente quien reportó la petición, "
                  + "según la regla de negocio número 2.2 del área de operación.";

        var salida = SimpleAnswerSanitizer.Sanitize(prosa);

        Assert.Equal(prosa, salida);
    }

    // ── Bordes ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EntradaVaciaONula_SeDevuelveTalCual(string? entrada)
    {
        Assert.Equal(entrada, SimpleAnswerSanitizer.Sanitize(entrada!));
    }

    [Fact]
    public void Idempotente_SanitizarDosVecesDaLoMismo()
    {
        // Importa porque el filtro corre por chunk en el camino de streaming: si no
        // fuera idempotente, un fragmento podría degradarse en cada pasada.
        var entrada = "El campo `IsAuditado` y la clase ServicioCliente aparecen aquí.";

        var unaVez = SimpleAnswerSanitizer.Sanitize(entrada);
        var dosVeces = SimpleAnswerSanitizer.Sanitize(unaVez);

        Assert.Equal(unaVez, dosVeces);
    }
}
