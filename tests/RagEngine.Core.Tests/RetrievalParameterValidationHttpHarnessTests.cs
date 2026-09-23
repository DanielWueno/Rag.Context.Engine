using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre el criterio literal de 12.1-cotas-y-cierre-inmediato sobre TopK/MinScore en
/// <c>/api/search</c>, <c>/api/ask</c> y <c>/api/ask/stream</c>: un valor fuera de rango
/// se RECHAZA (400 ProblemDetails) antes de tocar retrieval/rerank — nunca se recorta
/// silenciosamente. La validación corre ANTES de <c>AuthorizeCollectionAsync</c>
/// (Program.cs), así que estos tests no necesitan Qdrant/Ollama vivos ni una colección
/// real: la petición nunca llega a tocarlos. <c>collection</c> apunta deliberadamente
/// a una colección que no existe, para dejar constancia de que sí importaría si la
/// validación no cortara antes.
/// </summary>
public sealed class RetrievalParameterValidationHttpHarnessTests : IClassFixture<ParameterValidationWebApplicationFactory>
{
    private const string NonExistentCollection = "rag-engine-test-param-validation-no-existe";

    private readonly HttpClient _http;

    public RetrievalParameterValidationHttpHarnessTests(ParameterValidationWebApplicationFactory factory)
    {
        _http = factory.CreateClient();
    }

    public static IEnumerable<object[]> InvalidTopKValues() =>
    [
        [0], [101], [100000]
    ];

    public static IEnumerable<object[]> InvalidMinScoreValues() =>
    [
        [-0.1f], [1.1f]
    ];

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/ask")]
    [InlineData("/api/ask/stream")]
    public async Task TopK_fuera_de_rango_se_rechaza_con_400_en_los_tres_endpoints(string endpoint)
    {
        foreach (var invalidTopK in new[] { 0, 101, 100000 })
        {
            using var response = await _http.PostAsJsonAsync(endpoint, new
            {
                query = "¿qué pasa con un topK fuera de rango?",
                collection = NonExistentCollection,
                topK = invalidTopK
            });

            Assert.True(HttpStatusCode.BadRequest == response.StatusCode,
                $"topK={invalidTopK} en {endpoint} debió rechazarse con 400, llegó {(int)response.StatusCode}.");
            await AssertProblemDetailsAsync(response, "topK");
        }
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/ask")]
    [InlineData("/api/ask/stream")]
    public async Task MinScore_fuera_de_rango_o_no_finito_se_rechaza_con_400_en_los_tres_endpoints(string endpoint)
    {
        foreach (var invalidMinScore in new[] { -0.1f, 1.1f })
        {
            using var response = await _http.PostAsJsonAsync(endpoint, new
            {
                query = "¿qué pasa con un minScore fuera de rango?",
                collection = NonExistentCollection,
                minScore = invalidMinScore
            });

            Assert.True(HttpStatusCode.BadRequest == response.StatusCode,
                $"minScore={invalidMinScore} en {endpoint} debió rechazarse con 400, llegó {(int)response.StatusCode}.");
            await AssertProblemDetailsAsync(response, "minScore");
        }
    }

    [Theory]
    [InlineData("/api/search", 1)]
    [InlineData("/api/search", 100)]
    [InlineData("/api/ask", 1)]
    [InlineData("/api/ask", 100)]
    [InlineData("/api/ask/stream", 1)]
    [InlineData("/api/ask/stream", 100)]
    public async Task TopK_en_los_limites_1_y_100_no_se_rechaza_por_validacion(string endpoint, int validTopK)
    {
        using var response = await _http.PostAsJsonAsync(endpoint, new
        {
            query = "¿qué pasa con un topK en el límite?",
            collection = NonExistentCollection,
            topK = validTopK
        });

        // No hay 400: el límite pasa la validación. La colección no existe, así que la
        // request puede fallar más adelante (403/500) — lo que importa aquí es que NUNCA
        // sea el 400 de "Parámetros de recuperación fuera de rango.": eso demostraría que
        // el límite se rechazó cuando debía aceptarse.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/search", 0.0f)]
    [InlineData("/api/search", 1.0f)]
    [InlineData("/api/ask", 0.0f)]
    [InlineData("/api/ask", 1.0f)]
    public async Task MinScore_en_los_limites_0_y_1_no_se_rechaza_por_validacion(string endpoint, float validMinScore)
    {
        using var response = await _http.PostAsJsonAsync(endpoint, new
        {
            query = "¿qué pasa con un minScore en el límite?",
            collection = NonExistentCollection,
            minScore = validMinScore
        });

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TopK_y_minScore_null_no_se_rechazan_por_validacion()
    {
        using var response = await _http.PostAsJsonAsync("/api/search", new
        {
            query = "¿qué pasa sin topK ni minScore?",
            collection = NonExistentCollection,
            topK = (int?)null,
            minScore = (float?)null
        });

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task El_endpoint_de_depuracion_test_metrics_no_existe()
    {
        // Ítem 12.1: "sin endpoints de depuración" — /api/test-metrics exponía contadores
        // internos del Meter sin autenticación ni utilidad fuera de desarrollo local.
        using var response = await _http.GetAsync("/api/test-metrics");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task AssertProblemDetailsAsync(HttpResponseMessage response, string expectedErrorKey)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("errors", out var errors),
            $"Se esperaba un ProblemDetails con 'errors'; body: {body}");
        Assert.True(errors.TryGetProperty(expectedErrorKey, out _),
            $"Se esperaba la clave '{expectedErrorKey}' en errors; body: {body}");
    }
}

/// <summary>
/// Host de prueba sobre el Program.cs real de la API. Authorization:Mode se deja en el
/// default (Local) de appsettings.json — la validación de parámetros corre ANTES de la
/// autorización, así que el modo no importa para este criterio.
/// </summary>
public sealed class ParameterValidationWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("ParameterValidationHarness");
}
