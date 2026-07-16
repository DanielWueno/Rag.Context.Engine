using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RagEngine.Cli.Infrastructure;

/// <summary>
/// Bridges an already-built Microsoft.Extensions.DependencyInjection IServiceProvider
/// to Spectre.Console.Cli's type registration mechanism.
///
/// Unlike SpectreTypeRegistrar (which builds a new ServiceProvider from a fresh
/// IServiceCollection), this registrar forwards resolution to the host's existing
/// provider — meaning all services registered in Program.cs are available to commands.
/// </summary>
public sealed class SpectreHostTypeRegistrar : ITypeRegistrar
{
    private readonly IServiceProvider _provider;
    private readonly IServiceCollection _extras = new ServiceCollection();

    public SpectreHostTypeRegistrar(IServiceProvider provider)
    {
        _provider = provider;
    }

    public ITypeResolver Build()
        => new SpectreHostTypeResolver(_provider, _extras);

    public void Register(Type service, Type implementation)
        => _extras.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation)
        => _extras.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory)
        => _extras.AddSingleton(service, _ => factory());
}

/// <summary>
/// Resolves Spectre.Console command types first from the host's DI container,
/// falling back to an extras collection for Spectre-internal registrations.
/// </summary>
public sealed class SpectreHostTypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _hostProvider;
    private readonly ServiceProvider _extrasProvider;

    public SpectreHostTypeResolver(IServiceProvider hostProvider, IServiceCollection extras)
    {
        _hostProvider = hostProvider;
        _extrasProvider = extras.BuildServiceProvider();
    }

    public object? Resolve(Type? type)
    {
        if (type is null) return null;

        // Try the host container first (has all app services)
        var result = _hostProvider.GetService(type);
        if (result is not null) return result;

        // Fall back to Spectre-internal registrations
        return _extrasProvider.GetService(type);
    }

    public void Dispose() => _extrasProvider.Dispose();
}
