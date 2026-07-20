using System.Text.Json;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Caché en JSON de resúmenes, keyeada por (ContentHash, PromptVersion) — el mismo diseño
/// del Reto A del documento, pero en un archivo plano (no SQLite) porque para el PoC basta.
/// Evita re-generar 900+ resúmenes con Ollama en cada corrida mientras se itera el eval set.
///
/// Convención de valor:
///   • texto del resumen  → resumen de negocio real.
///   • cadena vacía ""    → centinela SIN_CONTENIDO_DE_NEGOCIO (no hay resumen, pero es un hit).
///   • ausente            → nunca generado o falló (se reintenta; los fallos no se cachean).
/// </summary>
public sealed class SummaryCache
{
    private readonly string _path;
    private readonly string _promptVersion;
    private readonly Dictionary<string, string> _entries;
    private readonly object _lock = new(); // la generación es concurrente
    private int _hits;

    public int Hits { get { lock (_lock) return _hits; } }

    private SummaryCache(string path, string promptVersion, Dictionary<string, string> entries)
    {
        _path = path;
        _promptVersion = promptVersion;
        _entries = entries;
    }

    public static SummaryCache Load(string path, string promptVersion)
    {
        var entries = new Dictionary<string, string>();
        if (File.Exists(path))
        {
            try
            {
                entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                          ?? new Dictionary<string, string>();
            }
            catch
            {
                // Caché corrupto → arrancar limpio, no abortar.
                entries = new Dictionary<string, string>();
            }
        }
        return new SummaryCache(path, promptVersion, entries);
    }

    private string Key(string contentHash) => $"{contentHash}|{_promptVersion}";

    /// <summary>Hit → devuelve (encontrado=true, resumen o null si fue sentinel). Miss → (false, null).</summary>
    public (bool Found, string? Summary) TryGet(string contentHash)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(Key(contentHash), out var value))
            {
                _hits++;
                return (true, value.Length == 0 ? null : value);
            }
            return (false, null);
        }
    }

    /// <summary>Guarda un resumen (o cadena vacía para el centinela). No llamar en fallos.</summary>
    public void Set(string contentHash, string? summaryOrSentinel)
    {
        lock (_lock) _entries[Key(contentHash)] = summaryOrSentinel ?? "";
    }

    public void Save()
    {
        lock (_lock)
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = false }));
    }
}
