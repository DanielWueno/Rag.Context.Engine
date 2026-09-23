namespace RagEngine.Core.Infrastructure.Transport;

/// <summary>
/// Ítem 12.8: declara si este host se considera "publicado" (alcanzable más allá del
/// loopback de una sola máquina de confianza) para exigir credencial y TLS antes de
/// arrancar. El default (<c>Published = false</c>) preserva el perfil local de siempre:
/// HTTP simple, sin exigir ninguna de las dos cosas — igual que antes de este ítem.
/// </summary>
public sealed record TransportOptions
{
    public const string SectionName = "Transport";

    /// <summary>
    /// <c>false</c> (default): perfil local — este proceso asume que solo lo alcanza
    /// tráfico HTTP en loopback (o un túnel/VPN de confianza ya cerrado), sin TLS propio.
    /// <c>true</c>: perfil publicado — exige <see cref="TransportOptionsValidator"/>
    /// que <c>Qdrant:ApiKey</c> esté configurado y que TLS quede resuelto, en el proceso
    /// (certificado de Kestrel) o corriente arriba (<see cref="TlsTerminatedUpstream"/>).
    /// </summary>
    public bool Published { get; init; }

    /// <summary>
    /// Solo se lee cuando <see cref="Published"/> es <c>true</c>. Reconoce explícitamente
    /// que un proxy/Ingress externo termina TLS antes de reenviar HTTP simple a este
    /// proceso — no activa nada por sí sola: sigue siendo responsabilidad de quien
    /// despliega configurar ese proxy con un certificado válido. Ver
    /// docs/configuracion.md y docs/operaciones.md para el perfil probado con un
    /// certificado local de prueba.
    /// </summary>
    public bool TlsTerminatedUpstream { get; init; }
}
