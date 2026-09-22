using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RagEngine.Core.Infrastructure.VectorStore.Expansion;

namespace RagEngine.Experiment152;

/// <summary>
/// Brazo "semantic": cualifica el destino con un SemanticModel real, es decir con las
/// referencias de metadatos del proyecto ya restauradas. Es el brazo que estuvo bloqueado
/// hasta resolver la autenticacion del feed privado: sin referencias, GetSymbolInfo
/// devuelve null para casi todo y el brazo mediria la falta de paquetes, no la capacidad
/// del mecanismo.
///
/// La compilacion se arma a mano (arboles + MetadataReference) en vez de con
/// MSBuildWorkspace: sus items Compile y ReferencePath se extraen invocando msbuild una
/// vez por proyecto, y ese coste entra en build_seconds del brazo en lugar de esconderse.
/// </summary>
public sealed class SemanticQualificationBuilder
{
    private readonly string _corpusRoot;

    public SemanticQualificationBuilder(string corpusRoot) => _corpusRoot = corpusRoot;

    public int ProjectsCompiled { get; private set; }
    public int ProjectsFailed { get; private set; }
    public int ChunksLocated { get; private set; }
    public int ChunksUnlocatable { get; private set; }
    public int InvocationsSeen { get; private set; }
    public int InvocationsResolved { get; private set; }
    public int InvocationsExternal { get; private set; }
    public List<string> Diagnostics { get; } = new();
    public Dictionary<string, int> CompilationErrors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ErrorCodes { get; } = new(StringComparer.Ordinal);

    private sealed record ProjectInputs(string Name, List<string> Sources, List<string> References);

    /// <summary>
    /// Extrae items Compile y ReferencePath de un csproj sin construirlo dos veces.
    /// MSBuildEnableWorkloadResolver=false es un override LOCAL al proceso: evita MSB4242
    /// por manifiestos de workload ausentes sin tocar el SDK global de la maquina.
    /// </summary>
    private static ProjectInputs? Evaluate(string csproj, string targetsFile, string scratch)
    {
        var name = Path.GetFileNameWithoutExtension(csproj);
        var sourcesOut = Path.Combine(scratch, name + ".sources.txt");
        var refsOut = Path.Combine(scratch, name + ".refs.txt");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(csproj)!,
        };
        foreach (var argument in new[]
                 {
                     "msbuild", csproj, "-t:DumpExperimentInputs",
                     "-p:CustomAfterMicrosoftCommonTargets=" + targetsFile,
                     "-p:ExperimentSourcesOut=" + sourcesOut,
                     "-p:ExperimentRefsOut=" + refsOut,
                     "-v:q", "-nologo",
                 })
        {
            psi.ArgumentList.Add(argument);
        }
        psi.Environment["MSBuildEnableWorkloadResolver"] = "false";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(sourcesOut) || !File.Exists(refsOut))
            throw new InvalidOperationException($"Evaluacion MSBuild fallida para {name}: exit {process.ExitCode} {error}");

        var sources = File.ReadAllLines(sourcesOut).Where(l => l.Length > 0 && File.Exists(l)).Distinct().ToList();
        var references = File.ReadAllLines(refsOut).Where(l => l.Length > 0 && File.Exists(l)).Distinct().ToList();
        return sources.Count == 0 ? null : new ProjectInputs(name, sources, references);
    }

    public Dictionary<string, IReadOnlyList<QualifiedSymbolTarget>> Qualify(
        IEnumerable<string> projectFiles, IEnumerable<ChunkPayload> chunks, string scratch)
    {
        var projects = projectFiles.ToList();
        Directory.CreateDirectory(scratch);
        var targetsFile = Path.Combine(scratch, "DumpExperimentInputs.targets");
        File.WriteAllText(targetsFile, """
            <Project>
              <Target Name="DumpExperimentInputs" DependsOnTargets="ResolveReferences">
                <WriteLinesToFile File="$(ExperimentSourcesOut)" Lines="@(Compile->'%(FullPath)')" Overwrite="true" />
                <WriteLinesToFile File="$(ExperimentRefsOut)" Lines="@(ReferencePath)" Overwrite="true" />
              </Target>
            </Project>
            """);

        var byPath = chunks.Where(c => c.RelativePath is not null && c.Content is not null)
                           .GroupBy(c => c.RelativePath!, StringComparer.Ordinal)
                           .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var result = new Dictionary<string, IReadOnlyList<QualifiedSymbolTarget>>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        // Nombres de ensamblado del corpus: todo lo demas (DevExpress, BCL) es externo.
        var corpusAssemblies = new HashSet<string>(
            projects.Select(Path.GetFileNameWithoutExtension)!, StringComparer.Ordinal);

        foreach (var csproj in projects)
        {
            ProjectInputs? inputs;
            try
            {
                inputs = Evaluate(csproj, targetsFile, scratch);
            }
            catch (Exception ex)
            {
                ProjectsFailed++;
                Diagnostics.Add(ex.Message);
                continue;
            }

            if (inputs is null) continue;

            var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
            foreach (var source in inputs.Sources)
            {
                var text = File.ReadAllText(source);
                trees[source] = CSharpSyntaxTree.ParseText(text, path: source);
            }

            var compilation = CSharpCompilation.Create(
                inputs.Name,
                trees.Values,
                inputs.References.Select(r => MetadataReference.CreateFromFile(r)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                                             allowUnsafe: true,
                                             nullableContextOptions: NullableContextOptions.Disable));
            ProjectsCompiled++;

            // La ficha prohibe convertir un fallo de compilacion en una abstencion: si el
            // SemanticModel no resuelve porque a la compilacion le faltan referencias, eso es
            // evidencia incompleta, no una limitacion del mecanismo. Se cuenta y se reporta.
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            CompilationErrors[inputs.Name] = errors.Count;
            foreach (var code in errors.Select(e => e.Id))
                ErrorCodes[code] = ErrorCodes.GetValueOrDefault(code) + 1;

            foreach (var (source, tree) in trees)
            {
                var relative = Path.GetRelativePath(_corpusRoot, source).Replace('\\', '/');
                if (!byPath.TryGetValue(relative, out var fileChunks)) continue;
                // Un archivo compartido por varios proyectos se cualifica una sola vez, con el
                // primero que lo reclama: dos compilaciones del mismo archivo darian el mismo
                // simbolo y duplicar el trabajo solo inflaria build_seconds.
                if (!claimed.Add(relative)) continue;

                var model = compilation.GetSemanticModel(tree);
                var text = tree.GetText().ToString();
                var root = tree.GetRoot();

                foreach (var chunk in fileChunks)
                {
                    var span = SourceSpanLocator.Locate(text, chunk.Content!);
                    if (span is null) { ChunksUnlocatable++; continue; }
                    ChunksLocated++;

                    var textSpan = TextSpan.FromBounds(span.Value.Start, Math.Min(span.Value.End + 1, text.Length));
                    var targets = new List<QualifiedSymbolTarget>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var invocation in root.DescendantNodes()
                                                   .OfType<InvocationExpressionSyntax>()
                                                   .Where(n => textSpan.Contains(n.SpanStart)))
                    {
                        InvocationsSeen++;
                        var info = model.GetSymbolInfo(invocation);
                        var symbol = info.Symbol as IMethodSymbol
                                     ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                        if (symbol is null) continue;

                        // Solo destinos que pertenecen al CORPUS: una llamada al framework se
                        // resuelve perfectamente y aun asi no tiene chunk al que saltar.
                        //
                        // El criterio es el ensamblado, no Locations.IsInSource: un destino de
                        // OTRO proyecto del corpus entra en esta compilacion como metadato
                        // (Backbone.dll), no como fuente, y filtrar por IsInSource descartaba
                        // silenciosamente todos los saltos entre modulos - justo los que el
                        // eval-set llama "salto". Medido: con IsInSource el brazo se abstenia
                        // en q55-entry, cuyo destino real vive en otro proyecto.
                        var assembly = symbol.ContainingAssembly?.Name;
                        if (assembly is null || !corpusAssemblies.Contains(assembly))
                        {
                            InvocationsExternal++;
                            continue;
                        }

                        var containing = symbol.ContainingType?.Name;
                        if (containing is null) continue;

                        InvocationsResolved++;
                        var name = symbol.Name;
                        if (seen.Add(containing + " " + name))
                            targets.Add(new QualifiedSymbolTarget(containing, name));
                    }

                    result[chunk.Id] = targets;
                }
            }
        }

        return result;
    }
}
