// Fixture deliberada para el control rojo de 9.5: NUNCA se compila (ver
// <Compile Remove="Mutants\**\*.cs" /> en el .csproj). Simula el mutante que la
// regla debe atrapar: una clase de "aplicación" que nombra un adaptador concreto
// directamente en vez de depender sólo de su puerto en Abstractions/.
using Microsoft.SemanticKernel;
using Qdrant.Client;

namespace RagEngine.Architecture.Tests.Mutants;

internal sealed class FakeApplicationServiceThatShouldNotExist
{
    public QdrantClient? Client { get; set; }
    public Kernel? Kernel { get; set; }
}
