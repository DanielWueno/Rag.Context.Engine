using System.Security.Cryptography.X509Certificates;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure;

/// <summary>
/// Único punto que construye el <see cref="HttpClient"/> usado para hablar con Ollama
/// (local o Servidor.IA) — lo comparten <c>GenerationServiceExtensions</c> (Kernel de
/// conversación) y <c>OllamaBusinessSummaryGenerator</c> (Kernel de resúmenes). Antes cada
/// uno construía el suyo por separado con la misma clave ficticia "ollama" y la confianza
/// TLS del sistema, lo que hacía imposible apuntar a un endpoint https con CA propia sin
/// duplicar la lógica en dos sitios (ítem 10.6.2, porte del 17.1 local).
/// </summary>
public static class OllamaHttpClientFactory
{
    /// <summary>
    /// Clave ficticia que exige <c>AddOpenAIChatCompletion</c> (el parámetro apiKey es
    /// obligatorio) cuando no hay credencial real — Ollama local la ignora por completo.
    /// </summary>
    public const string LocalPlaceholderApiKey = "ollama";

    /// <summary>
    /// Valida que la configuración sea coherente ANTES de construir nada. Se invoca desde
    /// los dos sitios que construyen un Kernel de Ollama (GenerationServiceExtensions y
    /// OllamaBusinessSummaryGenerator), y también desde <see cref="OllamaOptionsValidator"/>
    /// (ítem 10.6.2) para el fail-fast de arranque de la Api vía
    /// <c>AddOptions&lt;OllamaOptions&gt;().ValidateOnStart()</c>. Una configuración a medias
    /// (https sin clave, o una CA que no existe en disco) falla con un mensaje claro en vez
    /// de un 401/403 silencioso o una excepción de validación de certificado sin contexto.
    /// </summary>
    public static void Validate(OllamaOptions opts)
    {
        if (Uri.TryCreate(opts.Endpoint, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            throw new InvalidOperationException(
                $"Ollama:Endpoint ('{opts.Endpoint}') es https pero falta Ollama:ApiKey. " +
                "Define la variable de entorno Ollama__ApiKey antes de arrancar (perfil servidor: " +
                "se alimenta de SERVIDOR_IA_CLAVE). No se arranca con TLS sin autenticación.");
        }

        if (!string.IsNullOrWhiteSpace(opts.CaCertificatePath) && !File.Exists(opts.CaCertificatePath))
        {
            throw new InvalidOperationException(
                $"Ollama:CaCertificatePath apunta a un archivo que no existe: '{opts.CaCertificatePath}'. " +
                "Verifica la variable de entorno Ollama__CaCertificatePath (perfil servidor: SERVIDOR_IA_CA).");
        }
    }

    /// <summary>
    /// Resuelve la clave que se le pasa al parámetro <c>apiKey</c> de
    /// <c>AddOpenAIChatCompletion</c> — y NO a <see cref="HttpClient.DefaultRequestHeaders"/>:
    /// el conector OpenAI de Semantic Kernel fija la cabecera <c>Authorization</c>
    /// explícitamente en cada petición a partir de ese parámetro, pisando sin aviso
    /// cualquier valor puesto de antemano en DefaultRequestHeaders (verificado de forma
    /// empírica: un HttpClient con DefaultRequestHeaders.Authorization distinto del
    /// apiKey del builder termina enviando el del builder, no el del cliente). Por eso
    /// este es el único lugar que decide qué credencial llega al conector — real si hay
    /// <see cref="OllamaOptions.ApiKey"/>, o <see cref="LocalPlaceholderApiKey"/> si no.
    /// </summary>
    public static string ResolveSdkApiKey(OllamaOptions opts) =>
        string.IsNullOrWhiteSpace(opts.ApiKey) ? LocalPlaceholderApiKey : opts.ApiKey;

    /// <summary>
    /// Construye el <see cref="HttpClient"/> compartido. <paramref name="transportHandler"/>
    /// solo existe para que los tests puedan sustituir el transporte real (p. ej. un
    /// handler en memoria que simula un 401) sin dejar de ejercitar esta misma fábrica;
    /// en producción siempre es null y se usa un <see cref="HttpClientHandler"/> real con,
    /// si corresponde, la validación de cadena contra la CA propia.
    /// </summary>
    public static HttpClient Create(OllamaOptions opts, HttpMessageHandler? transportHandler = null)
    {
        Validate(opts);

        var handler = transportHandler ?? BuildTlsHandler(opts);

        var httpClient = new HttpClient(handler, disposeHandler: transportHandler is null)
        {
            BaseAddress = new Uri(opts.Endpoint),
            Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds),
        };

        return httpClient;
    }

    /// <summary>
    /// Handler real usado en producción. Si hay CaCertificatePath, valida la cadena del
    /// certificado del servidor EXCLUSIVAMENTE contra esa CA (CustomRootTrust +
    /// CustomTrustStore) — nunca un callback que acepte cualquier certificado. Sin
    /// CaCertificatePath (perfil local, http), se usa la confianza TLS del sistema sin
    /// tocar nada, igual que antes de este ítem.
    /// </summary>
    private static HttpMessageHandler BuildTlsHandler(OllamaOptions opts)
    {
        var httpHandler = new HttpClientHandler();

        if (string.IsNullOrWhiteSpace(opts.CaCertificatePath))
            return httpHandler;

        // Cargado una sola vez por HttpClient construido (no por request): el callback
        // de abajo solo referencia este certificado ya en memoria.
        var trustedRoot = X509CertificateLoader.LoadCertificateFromFile(opts.CaCertificatePath);

        httpHandler.ServerCertificateCustomValidationCallback = (_, certificate, chain, _) =>
        {
            if (certificate is null || chain is null)
                return false;

            // CustomRootTrust: SOLO los certificados en CustomTrustStore se consideran
            // raíces de confianza para esta validación — ni el almacén del sistema, ni
            // "aceptar siempre" (eso prohíbe la ficha explícitamente). Un certificado
            // válido pero firmado por cualquier otra CA falla a construir la cadena.
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Clear();
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            return chain.Build(certificate);
        };

        return httpHandler;
    }
}
