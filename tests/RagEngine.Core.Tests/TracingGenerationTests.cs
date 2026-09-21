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
/// </summary>
public sealed class TracingGenerationTests
{
    [Fact]
    public async Task Turno_con_anclaje_produce_gate_contexto_y_generacion_bajo_el_mismo_trace_id_que_la_auditoria()
    {
        await using var factory = new GenerationHttpHarnessTests.GenerationFactory();
        using var http = factory.CreateClient();
        var captured = new List<Activity>();
        using (Listen(captured))
        {
            using var response = await http.PostAsJsonAsync("/api/ask", new { query = "high", collection = "fixture" });
            response.EnsureSuccessStatusCode();
        }

        var turn = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Turn);
        var gate = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Gate);
        var context = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Context);
        var generation = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Generation);
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
        using (Listen(captured))
        {
            using var response = await http.PostAsJsonAsync("/api/ask", new { query = "meta", collection = "fixture" });
            response.EnsureSuccessStatusCode();
        }

        var turn = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Turn);
        Assert.DoesNotContain(captured, a => a.OperationName != RagEngineTracing.Steps.Turn);
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
        using (Listen(captured))
        {
            using var response = await http.PostAsJsonAsync("/api/ask", new { query = "empty", collection = "fixture" });
            response.EnsureSuccessStatusCode();
        }

        var gate = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Gate);
        Assert.DoesNotContain(captured, a => a.OperationName == RagEngineTracing.Steps.Context);
        var generation = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Generation);
        Assert.Equal(gate.TraceId, generation.TraceId);
        Assert.Equal("False", Tag(gate, "rag.has_grounding"));
        var skip = Assert.Single(generation.Parent switch { null => captured, var p => [p] },
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
        using (Listen(captured))
        {
            using var response = await http.PostAsJsonAsync("/api/ask",
                new { query = "high", collection = "fixture", responseMode = "technical" });
        }

        var turn = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Turn);
        var generation = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Generation);
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
        using (Listen(captured))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask/stream")
            {
                Content = JsonContent.Create(new { query = "high", collection = "fixture", responseMode = "technical" })
            };
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

        var turn = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Turn);
        var generation = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Generation);
        Assert.Equal(ActivityStatusCode.Error, generation.Status);
        Assert.Equal("cancelled", generation.StatusDescription);
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
        Assert.Equal("cancelled", turn.StatusDescription);
    }

    private static string? Tag(Activity activity, string key) =>
        activity.TagObjects.FirstOrDefault(t => t.Key == key).Value?.ToString();

    private static IDisposable Listen(List<Activity> captured)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RagEngineTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (captured) captured.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
