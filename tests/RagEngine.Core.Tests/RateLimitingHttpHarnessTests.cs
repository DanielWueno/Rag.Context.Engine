using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre el criterio literal de rate limiting de 12.1-cotas-y-cierre-inmediato: con
/// <c>RateLimiting:PermitLimit=2</c> (ventana de 60s, sin cola), la TERCERA solicitud del
/// mismo actor a <c>/api/search</c> recibe 429 y — el punto que un simple curl externo NO
/// puede demostrar — <see cref="ISemanticRetriever.SearchAsync"/> se invoca EXACTAMENTE 2
/// veces, nunca 3: el rechazo corta en el <c>EndpointFilter</c> de Program.cs ANTES de
/// que el handler real (y por tanto retrieval) se ejecute.
///
/// Espía de retrieval en vez de Qdrant real: <see cref="ISemanticRetriever"/> se
/// reemplaza por <see cref="CountingSemanticRetriever"/> vía <c>ConfigureTestServices</c>,
/// así que "invocaciones" se cuenta exactamente, sin depender de latencia/reintentos de
/// un backend real. La autorización (Program.AuthorizeCollectionAsync) sí sigue tocando
/// Qdrant local real para <c>GetManifestAsync</c> de una colección aleatoria que nunca se
/// crea — igual que el resto de harnesses HTTP de este proyecto — pero eso ocurre
/// DESPUÉS del filtro de rate limiting, así que no lo confunde con el conteo de retrieval.
/// </summary>
public sealed class RateLimitingHttpHarnessTests
{
    [Fact]
    public async Task Tercera_solicitud_del_mismo_actor_da_429_y_cero_invocaciones_extra_de_retrieval()
    {
        var retriever = new CountingSemanticRetriever();
        using var factory = new RateLimitedWebApplicationFactory(permitLimit: 2, windowSeconds: 60, retriever);
        using var http = factory.CreateClient();
        var collection = $"rag-engine-test-rate-limit-{Guid.NewGuid():N}";

        Task<HttpResponseMessage> Search() => http.PostAsJsonAsync("/api/search", new { query = "¿cuántas solicitudes lleva este actor?", collection, topK = 1 });

        using var first = await Search();
        using var second = await Search();
        using var third = await Search();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("application/problem+json", third.Content.Headers.ContentType?.MediaType);

        // El criterio literal: 2 solicitudes pasaron -> 2 invocaciones de retrieval. La
        // tercera, rechazada por el filtro, NO debe sumar una tercera invocación.
        Assert.Equal(2, retriever.InvocationCount);
    }

    [Fact]
    public async Task Dos_actores_distintos_no_comparten_la_misma_cuota()
    {
        var retriever = new CountingSemanticRetriever();
        using var factory = new RateLimitedWebApplicationFactory(permitLimit: 1, windowSeconds: 60, retriever);
        using var httpActorA = factory.CreateClient();
        using var httpActorB = factory.CreateClient();
        var collection = $"rag-engine-test-rate-limit-{Guid.NewGuid():N}";

        // La partición por defecto (sin identidad autenticada) cae en la IP remota; dos
        // HttpClient de la misma TestServer comparten esa IP simulada, así que se
        // distinguen aquí por X-Forwarded-For consumido por el TestActorIpMiddleware del
        // factory — sin esto, ambos "actores" caerían en la misma partición y este test
        // no probaría nada sobre aislamiento entre tenants/actores.
        httpActorA.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.0.0.1");
        httpActorB.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.0.0.2");

        using var actorAFirst = await httpActorA.PostAsJsonAsync("/api/search", new { query = "actor A", collection, topK = 1 });
        using var actorASecond = await httpActorA.PostAsJsonAsync("/api/search", new { query = "actor A otra vez", collection, topK = 1 });
        using var actorBFirst = await httpActorB.PostAsJsonAsync("/api/search", new { query = "actor B", collection, topK = 1 });

        Assert.Equal(HttpStatusCode.OK, actorAFirst.StatusCode);
        // Ningún límite GLOBAL: el actor A agotando su propia cuota (PermitLimit=1) no
        // bloquea al actor B, que llega con su primera solicitud.
        Assert.Equal(HttpStatusCode.TooManyRequests, actorASecond.StatusCode);
        Assert.Equal(HttpStatusCode.OK, actorBFirst.StatusCode);
    }

    /// <summary>
    /// Cuenta invocaciones de <see cref="SearchAsync"/> sin tocar Qdrant/ONNX — siempre
    /// devuelve una lista vacía, suficiente para que <c>/api/search</c> complete 200.
    /// </summary>
    private sealed class CountingSemanticRetriever : ISemanticRetriever
    {
        private int _invocationCount;

        public int InvocationCount => _invocationCount;

        public Task<IReadOnlyList<RetrievalResult>> SearchAsync(
            string query, RetrievalOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return Task.FromResult<IReadOnlyList<RetrievalResult>>([]);
        }
    }

    private sealed class RateLimitedWebApplicationFactory(int permitLimit, int windowSeconds, ISemanticRetriever retriever)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("RateLimitingHarness");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:PermitLimit"] = permitLimit.ToString(),
                    ["RateLimiting:WindowSeconds"] = windowSeconds.ToString(),
                    ["RateLimiting:QueueLimit"] = "0"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ISemanticRetriever>(retriever);
                services.AddSingleton<IStartupFilter>(new ClientIpStartupFilter());
            });
        }
    }

    /// <summary>
    /// Sustituye <c>HttpContext.Connection.RemoteIpAddress</c> por el valor de
    /// <c>X-Test-Client-Ip</c> — TestServer (in-memory) deja esa IP en null por defecto,
    /// así que sin esto, dos HttpClient del mismo factory caerían en la misma partición
    /// de rate limiting y <see cref="RateLimitingHttpHarnessTests.Dos_actores_distintos_no_comparten_la_misma_cuota"/>
    /// no podría distinguir actores.
    /// </summary>
    private sealed class ClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Client-Ip", out var ip) &&
                    IPAddress.TryParse(ip.ToString(), out var parsedIp))
                {
                    context.Connection.RemoteIpAddress = parsedIp;
                }
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
