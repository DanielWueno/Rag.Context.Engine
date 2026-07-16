using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Services.Generation;

namespace RagEngine.Core.Extensions;

// ─────────────────────────────────────────────────────────────────────────────
//  Typed options — bound from the "Ollama" section of appsettings.json
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Configuration options for the local Ollama LLM endpoint.
/// Bound from appsettings.json → "Ollama" section.
/// </summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Base URL of the Ollama OpenAI-compatible API endpoint.</summary>
    public string Endpoint { get; init; } = "http://localhost:11434/v1";

    /// <summary>Model tag to use (must be pulled in Ollama beforehand).</summary>
    public string ModelId { get; init; } = "qwen2.5-coder";

    /// <summary>Request timeout in seconds for long LLM generations.</summary>
    public int TimeoutSeconds { get; init; } = 120;
}

// ─────────────────────────────────────────────────────────────────────────────
//  Generation DI extension  (separate from AddRagEngineCore to keep
//  the Core extension focused on infrastructure and allow hosts that
//  don't need LLM generation to opt out cleanly)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Registers the Semantic Kernel pipeline and the <see cref="IRagGenerationService"/>
/// implementation. Connects to a local Ollama instance using the OpenAI-compatible
/// API — no third-party Ollama SDK required.
/// </summary>
public static class GenerationServiceExtensions
{
    /// <summary>
    /// Adds the Semantic Kernel + Ollama connector and the RAG generation orchestrator.
    /// Must be called AFTER <c>AddRagEngineCore</c> since it depends on
    /// <see cref="ISemanticRetriever"/> being already registered.
    /// </summary>
    public static IServiceCollection AddRagEngineGeneration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── 1. Bind typed options ─────────────────────────────────────────────
        services.Configure<OllamaOptions>(
            configuration.GetSection(OllamaOptions.SectionName));

        // ── 2. Register Semantic Kernel as a Singleton ────────────────────────
        //
        //  Why Singleton?
        //    • Kernel construction is NOT cheap: it validates the endpoint,
        //      creates the HttpClient pipeline, and sets up middleware.
        //    • The Kernel itself is stateless between calls — ChatHistory lives
        //      on the stack inside RagGenerationService.AskStreamingAsync.
        //    • Matches how SK is documented for hosted-service scenarios.
        //
        //  Why AddOpenAIChatCompletion and not a dedicated Ollama package?
        //    • Ollama exposes a 100% OpenAI-compatible REST API at /v1.
        //    • SK 1.78.0 ships Connectors.OpenAI; no extra packages are needed.
        //    • The endpoint and apiKey ("ollama" is a dummy but required placeholder)
        //      are the only differences from a real OpenAI registration.
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(opts.Endpoint),
                Timeout     = TimeSpan.FromSeconds(opts.TimeoutSeconds)
            };

            var builder = Kernel.CreateBuilder();

            // AddOpenAIChatCompletion with a custom HttpClient pointing at Ollama.
            // "ollama" is a required-but-ignored API key for the local endpoint.
            builder.AddOpenAIChatCompletion(
                modelId:    opts.ModelId,
                apiKey:     "ollama",
                httpClient: httpClient);

            return builder.Build();
        });

        // ── 3. Register the RAG orchestrator ─────────────────────────────────
        //
        //  Scoped (not Singleton) because ISemanticRetriever is Scoped.
        //  Each CLI command execution gets its own scope (via SpectreHostTypeRegistrar),
        //  so this is effectively one instance per command invocation.
        services.AddScoped<IRagGenerationService, RagGenerationService>();

        return services;
    }
}
