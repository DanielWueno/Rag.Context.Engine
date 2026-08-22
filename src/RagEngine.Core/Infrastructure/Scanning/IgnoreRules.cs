using System.Text;
using System.Text.RegularExpressions;

namespace RagEngine.Core.Infrastructure.Scanning;

/// <summary>
/// Reglas de exclusión con sintaxis .gitignore, aplicadas al escaneo de ingesta.
///
/// Se cargan de dos archivos en la raíz del repositorio, en este orden:
///   1. <c>.gitignore</c> — lo que el repo ya declara como "no es fuente" (bin, obj,
///      logs, artefactos de build) tampoco es corpus. Evita duplicar esa lista a mano.
///   2. <c>.ragignore</c> — exclusiones propias del corpus: archivos versionados y
///      legítimos que aun así no aportan a la búsqueda semántica (baselines de
///      evaluación, volcados generados por máquina, datos de prueba).
///
/// Como <c>.ragignore</c> se aplica después, puede re-incluir con <c>!patrón</c> algo
/// que <c>.gitignore</c> excluye — la vía de escape para un corpus que sí quiere
/// indexar algo ignorado por git.
///
/// Soporta el subconjunto útil de la sintaxis: comentarios <c>#</c>, negación <c>!</c>,
/// anclaje a la raíz (<c>/build</c> o cualquier patrón con <c>/</c> intermedio),
/// coincidencia a cualquier nivel (<c>logs</c>), sólo-directorio (<c>logs/</c>),
/// comodines <c>*</c> y <c>?</c> que no cruzan <c>/</c>, y <c>**</c> que sí lo cruza.
/// Gana la ÚLTIMA regla que coincide, igual que en git.
/// </summary>
public sealed class IgnoreRules
{
    public const string RagIgnoreFileName = ".ragignore";
    public const string GitIgnoreFileName = ".gitignore";

    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly, string Source);

    private readonly List<Rule> _rules;

    /// <summary>Conjunto vacío: no excluye nada.</summary>
    public static IgnoreRules Empty { get; } = new([]);

    private IgnoreRules(List<Rule> rules) => _rules = rules;

    /// <summary>True si no se cargó ninguna regla (ni .gitignore ni .ragignore).</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Nombres de los archivos que aportaron reglas, para poder registrarlo.</summary>
    public IReadOnlyList<string> Sources =>
        _rules.Select(r => r.Source).Distinct().ToList();

    /// <summary>Número de reglas cargadas.</summary>
    public int Count => _rules.Count;

    /// <summary>Carga las reglas desde la raíz indicada. Un archivo ausente simplemente no aporta reglas.</summary>
    public static IgnoreRules Load(string rootPath)
    {
        var rules = new List<Rule>();
        foreach (var fileName in new[] { GitIgnoreFileName, RagIgnoreFileName })
        {
            var path = Path.Combine(rootPath, fileName);
            if (!File.Exists(path)) continue;

            foreach (var line in File.ReadLines(path))
            {
                var rule = ParseLine(line, fileName);
                if (rule is not null) rules.Add(rule);
            }
        }
        return new IgnoreRules(rules);
    }

    /// <summary>Construye reglas desde líneas en memoria (para pruebas).</summary>
    public static IgnoreRules FromLines(IEnumerable<string> lines, string source = RagIgnoreFileName) =>
        new(lines.Select(l => ParseLine(l, source)).Where(r => r is not null).ToList()!);

    /// <summary>
    /// ¿Está excluida esta ruta? <paramref name="relativePath"/> es relativa a la raíz
    /// del escaneo; los separadores de Windows se normalizan.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        if (_rules.Count == 0) return false;

        var path = relativePath.Replace('\\', '/').TrimStart('.', '/').TrimEnd('/');
        if (path.Length == 0) return false;

        // Los ancestros son siempre directorios: así "logs/" excluye logs/a/b.json
        // sin necesidad de que el patrón absorba descendientes.
        var ancestors = Ancestors(path);

        bool ignored = false;
        foreach (var rule in _rules)
        {
            bool hit =
                ((!rule.DirectoryOnly || isDirectory) && rule.Pattern.IsMatch(path))
                || ancestors.Any(rule.Pattern.IsMatch);

            if (hit) ignored = !rule.Negated;
        }
        return ignored;
    }

    /// <summary>
    /// True si alguna regla es una negación. El escáner lo consulta para decidir si
    /// puede podar un directorio entero: con negaciones presentes, algo de dentro
    /// podría estar re-incluido y hay que descender igualmente.
    /// </summary>
    public bool HasNegations => _rules.Any(r => r.Negated);

    private static List<string> Ancestors(string path)
    {
        var parts = path.Split('/');
        var result = new List<string>(Math.Max(0, parts.Length - 1));
        for (int i = 1; i < parts.Length; i++)
            result.Add(string.Join('/', parts, 0, i));
        return result;
    }

    private static Rule? ParseLine(string rawLine, string source)
    {
        var line = rawLine.TrimEnd();
        if (line.Length == 0 || line[0] == '#') return null;

        bool negated = line[0] == '!';
        if (negated) line = line[1..];
        if (line.Length == 0) return null;

        bool directoryOnly = line.EndsWith('/');
        if (directoryOnly) line = line[..^1];
        if (line.Length == 0) return null;

        // Un patrón se ancla a la raíz si empieza por '/' o si lleva una '/' interna
        // ("docs/eval"); si no la lleva ("logs", "*.tmp"), coincide a cualquier nivel.
        bool anchored = line.StartsWith('/') || line.TrimEnd('/').Contains('/');
        line = line.TrimStart('/');
        if (line.Length == 0) return null;

        var regex = new Regex(BuildRegex(line, anchored), RegexOptions.Compiled | RegexOptions.CultureInvariant);
        return new Rule(regex, negated, directoryOnly, source);
    }

    private static string BuildRegex(string glob, bool anchored)
    {
        var sb = new StringBuilder();
        sb.Append('^');
        // No anclado: puede empezar en cualquier segmento de la ruta.
        sb.Append(anchored ? "" : "(?:.*/)?");

        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                bool doubleStar = i + 1 < glob.Length && glob[i + 1] == '*';
                if (doubleStar)
                {
                    // "**/" consume cero o más segmentos; "**" suelto cruza separadores.
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 1;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }

        sb.Append('$');
        return sb.ToString();
    }
}
