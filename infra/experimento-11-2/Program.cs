using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using RagEngine.Cli.Commands;
using RagEngine.Cli.Infrastructure;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;
using RagEngine.Core.Infrastructure.Vectorization;
using RagEngine.Core.Infrastructure.VectorStore;
using Spectre.Console.Cli;

// Experimental host only: production commands, chunker and token instrumentation
// are reused without changing their options or the application's retrieval path.
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.GetFullPath("src/RagEngine.Cli/appsettings.json"))
    .AddEnvironmentVariables().Build();
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddRagEngineCore(config);
services.AddSingleton<OnnxVectorizationBrain>();
services.AddSingleton<IVectorizationBrain, ObservedBrain>();
services.AddScoped<QdrantSemanticRetriever>();
services.AddScoped<ISemanticRetriever, ObservedRetriever>();
services.AddTransient<IngestCommand>();
services.AddTransient<EvalCommand>();
int code;
try
{
    await using var provider = services.BuildServiceProvider();
    if (args is ["probe"])
    {
        var brain = provider.GetRequiredService<IVectorizationBrain>();
        var text = string.Join(" ", Enumerable.Repeat("public class BusinessRule return value", 200));
        var result = await brain.GenerateBatchEmbeddingsWithStatsAsync([text]);
        if (result.Embeddings[0].Length != 384 ||
            result.Embeddings[0].Any(x => !float.IsFinite(x)) ||
            result.Stats[0].TotalTokens <= 510)
            throw new InvalidOperationException("Probe does not exercise a finite 512-position embedding.");
        Console.WriteLine(JsonSerializer.Serialize(new { window = brain.MaxSequenceLength, result.Stats }));
        code = 0;
    }
    else
    {
        var app = new CommandApp(new SpectreHostTypeRegistrar(provider));
        app.Configure(c =>
        {
            c.AddCommand<IngestCommand>("ingest");
            c.AddCommand<EvalCommand>("eval");
            c.PropagateExceptions();
        });
        code = await app.RunAsync(args);
        // EvalCommand deliberately catches search errors. Evidence must fail closed.
        if (ObservedRetriever.Errors != 0) code = 1;
    }
}
finally
{
    OnnxRuntimeLifetime.Shutdown();
}
return code;

sealed class ObservedBrain(OnnxVectorizationBrain inner) : IVectorizationBrain
{
    private readonly object gate = new();
    public int EmbeddingDimensions => inner.EmbeddingDimensions;
    public int MaxSequenceLength => inner.MaxSequenceLength;
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => inner.GenerateEmbeddingAsync(text, cancellationToken);
    public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<string> texts, CancellationToken cancellationToken = default)
        => inner.GenerateBatchEmbeddingsAsync(texts, cancellationToken);
    public async Task<VectorizationBatchResult> GenerateBatchEmbeddingsWithStatsAsync(
        IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var inputs = texts.ToList();
        var result = await inner.GenerateBatchEmbeddingsWithStatsAsync(inputs, cancellationToken);
        var path = Environment.GetEnvironmentVariable("EXPERIMENT_TOKENS");
        if (path is not null)
        {
            var rows = inputs.Select((text, i) => JsonSerializer.Serialize(new
            {
                enriched_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                stats = result.Stats[i]
            }));
            lock (gate) File.AppendAllLines(path, rows);
        }
        return result;
    }
}

sealed class ObservedRetriever(
    QdrantSemanticRetriever fusion, QdrantClient client, IVectorizationBrain brain) : ISemanticRetriever
{
    public static int Errors;
    // Reuse the exact production payload mapper, not another interpretation of
    // file names/content. This reflection is confined to the experimental host.
    private static readonly MethodInfo Mapper = typeof(QdrantSemanticRetriever)
        .GetMethod("MapToRetrievalResult", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("Production payload mapper changed.");

    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        string query, RetrievalOptions options, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<RetrievalResult> hits;
            if (Environment.GetEnvironmentVariable("EXPERIMENT_MODE") == "dense")
            {
                var vector = await brain.GenerateEmbeddingAsync(query, cancellationToken);
                var points = await client.QueryAsync(
                    collectionName: options.CollectionName, query: vector,
                    usingVector: QdrantVectorStore.DenseVectorName,
                    limit: (ulong)options.TopK, scoreThreshold: options.MinimumSimilarityScore,
                    payloadSelector: new Qdrant.Client.Grpc.WithPayloadSelector { Enable = true },
                    cancellationToken: cancellationToken);
                hits = points.Select(p => (RetrievalResult)Mapper.Invoke(
                    null, [p, RetrievalScoreScale.CosineSimilarity])!).ToList();
            }
            else
                hits = await fusion.SearchAsync(query, options, cancellationToken);
            timer.Stop();
            File.AppendAllText(Environment.GetEnvironmentVariable("EXPERIMENT_TRACE")!,
                JsonSerializer.Serialize(new
                {
                    question = query, elapsed_ms = timer.Elapsed.TotalMilliseconds,
                    hits = hits.Select(h => new
                    {
                        h.ChunkId, h.ContentHash, h.Metadata.RelativeFilePath,
                        h.Metadata.ClassName, h.Metadata.MethodName, h.SimilarityScore,
                        score_scale = h.ScoreScale.ToString()
                    })
                }) + "\n");
            return hits;
        }
        catch
        {
            Interlocked.Increment(ref Errors);
            throw;
        }
    }
}
