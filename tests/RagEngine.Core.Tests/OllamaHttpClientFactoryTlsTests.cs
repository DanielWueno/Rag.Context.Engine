using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 10.6.2 (porte del 17.1 local), punto 3: el HttpClient compartido debe validar la
/// cadena del servidor EXCLUSIVAMENTE contra la CA configurada
/// (<c>X509ChainTrustMode.CustomRootTrust</c>), nunca con un callback que acepte cualquier
/// certificado. Estos tests levantan un servidor TLS real en loopback con certificados
/// efímeros generados en el propio test (dos pares CA+leaf distintos, vía
/// <see cref="CertificateRequest"/>) — nunca el root.crt real de Servidor.IA — para
/// ejercitar el handshake de verdad: un ServerCertificateCustomValidationCallback roto
/// (p. ej. "return true" siempre) pasaría cualquier test que solo mirara el código sin
/// hacer un handshake real.
/// </summary>
public class OllamaHttpClientFactoryTlsTests
{
    private static (X509Certificate2 Ca, string CaFilePath) CrearCaEfimera(string nombre)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=Test-Root-{nombre}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        var path = Path.Combine(Path.GetTempPath(), $"test-ca-{nombre}-{Guid.NewGuid():N}.crt");
        File.WriteAllBytes(path, ca.Export(X509ContentType.Cert));
        return (ca, path);
    }

    private static X509Certificate2 CrearLeafFirmadaPor(X509Certificate2 ca)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: true));

        // El rango de validez del leaf debe caer DENTRO del de la CA — calcularlo con un
        // DateTimeOffset.UtcNow propio (en vez de relativo a ca.NotAfter) es una carrera:
        // basta un tick de diferencia entre la creación de la CA y la del leaf para que
        // CertificateRequest.Create rechace un notAfter "posterior" al de la CA
        // (reproducido de forma empírica).
        using var leafSinClave = request.Create(
            ca, ca.NotBefore, ca.NotAfter.AddMinutes(-1), Guid.NewGuid().ToByteArray());

        // Create() devuelve el certificado sin la clave privada asociada: hace falta
        // recombinarla para que SslStream pueda usarlo como certificado de servidor. El
        // export/reimport vía PFX con X509KeyStorageFlags.Exportable (en vez de usar
        // leafConClave directamente, o reimportar sin flags) es obligatorio en Windows:
        // SChannel rechaza una clave RSA "efímera" (la que produce CopyWithPrivateKey /
        // CreateSelfSigned tal cual) para TLS de servidor con
        // "the platform does not support ephemeral keys" — verificado de forma empírica
        // contra este mismo runtime antes de este fix.
        var leafConClave = leafSinClave.CopyWithPrivateKey(rsa);
        return X509CertificateLoader.LoadPkcs12(
            leafConClave.Export(X509ContentType.Pfx),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Servidor TLS mínimo en loopback: acepta UNA conexión, completa el handshake con
    /// <paramref name="certificadoServidor"/> y, si el handshake no revienta del lado
    /// del cliente, responde "200 OK" a cualquier request.
    /// </summary>
    private static (int Port, Task ServerTask) IniciarServidorTls(X509Certificate2 certificadoServidor)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var tcpClient = await listener.AcceptTcpClientAsync();
            using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificadoServidor,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                });

                // Hay que drenar el request ANTES de escribir la respuesta y cerrar: si el
                // socket se cierra con bytes del cliente todavía sin leer en el buffer del
                // SO, Windows manda un RST en vez de un cierre ordenado — el cliente ve
                // exactamente "An established connection was aborted by the software in
                // your host machine" al leer la respuesta que sí se alcanzó a escribir
                // (reproducido de forma empírica construyendo este mismo test). No hace
                // falta un parser HTTP real — basta con leer hasta ver el fin de las
                // cabeceras (un GET no trae body).
                var requestBuffer = new byte[8192];
                var totalLeido = 0;
                while (totalLeido < requestBuffer.Length)
                {
                    var leido = await sslStream.ReadAsync(requestBuffer.AsMemory(totalLeido));
                    if (leido == 0) break; // el cliente cerró su lado de escritura
                    totalLeido += leido;
                    if (System.Text.Encoding.ASCII.GetString(requestBuffer, 0, totalLeido).Contains("\r\n\r\n", StringComparison.Ordinal))
                        break;
                }

                var respuesta = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray();
                await sslStream.WriteAsync(respuesta);
                await sslStream.FlushAsync();
            }
            catch
            {
                // Esperado en el caso de rechazo: el cliente aborta el handshake porque
                // su callback devolvió false. El test solo verifica el lado cliente.
            }
            finally
            {
                listener.Stop();
            }
        });

        return (port, serverTask);
    }

    [Fact]
    public async Task HttpClient_RechazaCertificadoFirmadoPorOtraCa()
    {
        var (caConfiable, rutaCaConfiable) = CrearCaEfimera("confiable");
        var (caAjena, rutaCaAjena) = CrearCaEfimera("ajena");
        using var leafFirmadaPorCaAjena = CrearLeafFirmadaPor(caAjena);

        try
        {
            var (port, serverTask) = IniciarServidorTls(leafFirmadaPorCaAjena);

            var opts = new OllamaOptions
            {
                Endpoint = $"https://127.0.0.1:{port}",
                ApiKey = "clave-sintetica-de-test",
                CaCertificatePath = rutaCaConfiable, // la CA que el cliente confía NO firmó este leaf
            };

            using var httpClient = OllamaHttpClientFactory.Create(opts);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await httpClient.GetAsync("/", cts.Token));

            await WaitQuietlyAsync(serverTask);
        }
        finally
        {
            caConfiable.Dispose();
            caAjena.Dispose();
            File.Delete(rutaCaConfiable);
            File.Delete(rutaCaAjena);
        }
    }

    [Fact]
    public async Task HttpClient_AceptaCertificadoFirmadoPorLaCaConfigurada()
    {
        var (ca, rutaCa) = CrearCaEfimera("propia");
        using var leafFirmadaPorEsaCa = CrearLeafFirmadaPor(ca);

        try
        {
            var (port, serverTask) = IniciarServidorTls(leafFirmadaPorEsaCa);

            var opts = new OllamaOptions
            {
                Endpoint = $"https://127.0.0.1:{port}",
                ApiKey = "clave-sintetica-de-test",
                CaCertificatePath = rutaCa,
            };

            using var httpClient = OllamaHttpClientFactory.Create(opts);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            using var response = await httpClient.GetAsync("/", cts.Token);
            Assert.True(response.IsSuccessStatusCode);

            await serverTask;
        }
        finally
        {
            ca.Dispose();
            File.Delete(rutaCa);
        }
    }

    private static async Task WaitQuietlyAsync(Task serverTask)
    {
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // El servidor ya se registró como "esperado" en su propio catch; esto solo
            // evita que una excepción residual del Task tumbe el test.
        }
    }
}
