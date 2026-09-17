// Fixture de control negativo: no debe disparar ninguna violación. Sólo referencia
// tipos propios (Abstractions/Domain), como un colaborador de aplicación legítimo.
using System.Threading.Tasks;

namespace RagEngine.Architecture.Tests.Mutants;

internal sealed class FakeApplicationServiceThatIsFine
{
    public Task DoWorkAsync() => Task.CompletedTask;
}
