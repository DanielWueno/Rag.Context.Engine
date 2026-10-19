using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Diagnostics;
using RagEngine.Core.Domain;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 13.4: contrato de los spans "rag.gate"/"rag.context"/"rag.generation" y del
/// span raíz "rag.turn" abierto por <c>Program.cs</c>, sobre el harness HTTP de
/// <see cref="GenerationHttpHarnessTests"/> (retriever y chat mockeados). Verifica
/// también que el <c>trace_id</c> del span raíz es el mismo <c>CorrelationId</c> que
/// queda en el registro de auditoría del turno.
///
/// Aislamiento bajo paralelismo real de xUnit (ítem 19.6): <c>ActivitySource.AddActivityListener</c>
/// es un registro GLOBAL del proceso — un listener de este archivo también recibe las
/// activities que genere, en paralelo, cualquier otra clase que escriba en la misma
/// <see cref="RagEngineTracing.SourceName"/> (p.ej. <see cref="TracingRetrievalTests"/>, que
/// abre su propio span "rag.turn" como padre externo). Reducir el paralelismo del
/// ensamblado "arreglaría" esto de forma genérica mezclando aislamiento con rendimiento.
/// En su lugar, cada test envía su propio header <c>traceparent</c> (W3C) con un
/// <see cref="ActivityTraceId"/> generado antes de la llamada: el middleware de hosting
/// de ASP.NET Core adopta ese trace id como el de la activity raíz de la petición entrante
/// (y por tanto el de "rag.turn" y sus hijos, que no pasan un padre explícito y heredan de
/// <c>Activity.Current</c>). Después de capturar, cada test filtra la lista compartida por
/// ese trace id conocido ANTES de hacer cualquier aserción — así una activity ajena con el
/// mismo <c>OperationName</c> pero otro trace id nunca entra en el <c>Assert.Single</c>.
/// </summary>
public sealed class TracingGenerationTests
{
    [Fact]
    public async Task Turno_con_anclaje_produce_gate_contexto_y_generacion_bajo_el_mismo_trace_id_que_la_auditoria()
    {
        await using var factory = new GenerationHttpHarnessTests.GenerationFactory();
        using var http = factory.CreateClient();
        var captured = new List<Activity>();
        var (traceparent, expectedTraceId) = NewTraceContext();
        using (Listen(captured))
        {
            using var request = Post("/api/ask", new { query = "high", collection = "fixture" }, traceparent);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        var mine = OwnActivities(captured, expectedTraceId);
        var turn = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Turn);
        var gate = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Gate);
        var context = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Context);
        var generation = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Generation);
        Assert.Equal(turn.TraceId, gate.TraceId);
        Assert.Equal(turn.TraceId, context.TraceId);
        Assert.Equal(turn.TraceId, generation.TraceId);
        Assert.Equal("True", Tag(gate, "rag.has_grounding"));
        Assert.Equal(ActivityStatusCode.Unset, generation.Status);

        var audit = factory.Services.GetRequiredService<IAuditEventStore>();
        var events = await audit.ListAsync(CancellationToken.None);
        var entry = Assert.Single(events, e => e.Operation == AuditOperations.QueryAsk
            && e.CorrelationId == turn.TraceId.ToString());
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
    }

    [Fact]
    public async Task Meta_intencion_declara_los_cinco_pasos_omitidos_sin_crear_spans_de_paso()
    {
        await using var factory = new GenerationHttpHarnessTests.GenerationFactory();
        using var http = factory.CreateClient();
        var captured = new List<Activity>();
        var (traceparent, expectedTraceId) = NewTraceContext();
        using (Listen(captured))
        {
            using var request = Post("/api/ask", new { query = "meta", collection = "fixture" }, traceparent);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        var mine = OwnActivities(captured, expectedTraceId);
        var turn = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Turn);
        Assert.DoesNotContain(mine, a => a.OperationName != RagEngineTracing.Steps.Turn);
        foreach (var step in new[]
        {
            RagEngineTracing.Steps.Retrieval, RagEngineTracing.Steps.Gate,
            RagEngineTracing.Steps.Context, RagEngineTracing.Steps.Generation
        })
        {
            var skip = Assert.Single(turn.Events, e => e.Name == $"{step}.skipped");
            Assert.Equal("meta_intent", skip.Tags.First(t => t.Key == "reason").Value);
        }
    }

    [Fact]
    public async Task Respuesta_sin_anclaje_declara_contexto_omitido_pero_igual_genera()
    {
        await using var factory = new GenerationHttpHarnessTests.GenerationFactory();
        using var http = factory.CreateClient();
        var captured = new List<Activity>();
        var (traceparent, expectedTraceId) = NewTraceContext();
        using (Listen(captured))
        {
            using var request = Post("/api/ask", new { query = "empty", collection = "fixture" }, traceparent);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        var mine = OwnActivities(captured, expectedTraceId);
        var gate = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Gate);
        Assert.DoesNotContain(mine, a => a.OperationName == RagEngineTracing.Steps.Context);
        var generation = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Generation);
        Assert.Equal(gate.TraceId, generation.TraceId);
        Assert.Equal("False", Tag(gate, "rag.has_grounding"));
        var skip = Assert.Single(generation.Parent switch { null => mine, var p => [p] },
            a => a.Events.Any(e => e.Name == "rag.context.skipped"));
        Assert.Equal("no_grounding", skip.Events.Single(e => e.Name == "rag.context.skipped")
            .Tags.First(t => t.Key == "reason").Value);
        Assert.Equal(ActivityStatusCode.Unset, generation.Status);
    }

    [Fact]
    public async Task Fallo_de_generacion_tras_el_primer_fragmento_marca_error_en_el_span()
    {
        await using var factory = new GenerationHttpHarnessTests.GenerationFactory();
        factory.Chat.FailAfterFirst = true;
        using var http = factory.CreateClient();
        var captured = new List<Activity>();
        var (traceparent, expectedTraceId) = NewTraceContext();
        using (Listen(captured))
        {
            using var request = Post("/api/ask",
                new { query = "high", collection = "fixture", responseMode = "technical" }, traceparent);
            using var response = await http.SendAsync(request);
        }

        var mine = OwnActivities(captured, expectedTraceId);
        var turn = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Turn);
        var generation = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Generation);
        Assert.Equal(ActivityStatusCode.Error, generation.Status);
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
    }

    [Fact]
    public async Task Cancelacion_a_mitad_del_streaming_marca_error_de_cancelacion_en_generacion_y_turno()
    {
        await using var factory = new GenerationHttpHarnessTests.GenerationFactory();
        factory.Chat.ReleaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = factory.CreateClient();
        var captured = new List<Activity>();
        var (traceparent, expectedTraceId) = NewTraceContext();
        using (Listen(captured))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var request = Post("/api/ask/stream",
                new { query = "high", collection = "fixture", responseMode = "technical" }, traceparent);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
            while (await reader.ReadLineAsync(cancellation.Token) is { } line && line != "event: token")
            {
            }
            cancellation.Cancel();
            reader.Dispose();
            response.Dispose();
            await factory.Chat.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Da tiempo a que el handler termine de cerrar los spans tras propagar la cancelación.
            await Task.Delay(200, CancellationToken.None);
        }

        var mine = OwnActivities(captured, expectedTraceId);
        var turn = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Turn);
        var generation = Assert.Single(mine, a => a.OperationName == RagEngineTracing.Steps.Generation);
        Assert.Equal(ActivityStatusCode.Error, generation.Status);
        Assert.Equal("cancelled", generation.StatusDescription);
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
        Assert.Equal("cancelled", turn.StatusDescription);
    }

    private static string? Tag(Activity activity, string key) =>
        activity.TagObjects.FirstOrDefault(t => t.Key == key).Value?.ToString();

    /// <summary>
    /// Genera un trace id W3C propio para este test y el header <c>traceparent</c> que lo
    /// transporta. ASP.NET Core adopta este trace id para la activity raíz de la petición
    /// entrante, así que "rag.turn" (que no pasa padre explícito) termina heredándolo.
    /// </summary>
    private static (string Header, ActivityTraceId TraceId) NewTraceContext()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return ($"00-{traceId}-{spanId}-01", traceId);
    }

    private static HttpRequestMessage Post(string url, object body, string traceparent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("traceparent", traceparent);
        return request;
    }

    /// <summary>
    /// Filtra la lista (potencialmente contaminada por otras clases concurrentes que
    /// escriben en la misma fuente) a solo las activities de ESTE turno.
    /// </summary>
    private static List<Activity> OwnActivities(List<Activity> captured, ActivityTraceId expectedTraceId)
    {
        lock (captured)
        {
            return captured.Where(a => a.TraceId == expectedTraceId).ToList();
        }
    }

    private static IDisposable Listen(List<Activity> captured)
    {
        // También hay que escuchar "Microsoft.AspNetCore" (el nombre del ActivitySource del host;
        // "Microsoft.AspNetCore.Hosting" es solo el del DiagnosticListener, no una fuente de actividades,
        // y escucharlo no activa nada: fallaba de forma intermitente solo bajo carga). El host (HostingApplicationDiagnostics)
        // solo crea su activity raíz "HttpRequestIn" -- y por tanto solo adopta el traceparent entrante
        // como su TraceId -- cuando ESA fuente tiene al menos un listener. Sin esto, el header que manda
        // Post() se ignora en silencio y "rag.turn" arranca con un TraceId nuevo sin relación con
        // expectedTraceId. No hace falta capturar esas activities (sólo que existan), así que
        // ActivityStopped las descarta explícitamente.
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is RagEngineTracing.SourceName or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == RagEngineTracing.SourceName)
                    lock (captured) captured.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
