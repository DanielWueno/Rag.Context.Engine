using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;
using RagEngine.Core.Infrastructure.Authorization;
using Xunit;

namespace RagEngine.Core.Tests;

public sealed class CollectionAuthorizationServiceTests
{
    private static readonly CollectionManifest ProtectedCollection = new()
    {
        CollectionName = "sistema-protegido",
        ModelName = "modelo-fixture",
        ModelOnnxSha256 = "hash-fixture",
        EmbeddingDimension = 8,
        RequiredScopes = ["rag.read.sistema", "rag.read.soporte"],
        Tenants = ["tenant-a", "tenant-b"]
    };

    private static readonly CollectionActor Administrator = new()
    {
        IsAdministrator = true,
        Tenant = "tenant-ajeno"
    };

    private static readonly CollectionActor AuthorizedUser = new()
    {
        Scopes = ["otro-scope", "rag.read.sistema"],
        Tenant = "tenant-a"
    };

    private readonly ICollectionAuthorizationService _authorization = new CollectionAuthorizationService();

    [Fact]
    public void Administrador_lee_la_coleccion_protegida_sin_scopes_ni_tenant_permitido()
    {
        Assert.Empty(Administrator.Scopes);
        Assert.DoesNotContain(Administrator.Tenant, ProtectedCollection.Tenants);
        Assert.True(_authorization.Authorize(ProtectedCollection, EnterpriseActor(Administrator)));
    }

    [Theory]
    [InlineData("rag.read.sistema", "tenant-a")]
    [InlineData("rag.read.soporte", "tenant-b")]
    public void Usuario_lee_con_scope_del_sistema_mas_tenant_permitido(string scope, string tenant)
    {
        var actor = AuthorizedUser with { Scopes = ["otro-scope", scope], Tenant = tenant };

        Assert.True(_authorization.Authorize(ProtectedCollection, EnterpriseActor(actor)));
    }

    [Theory]
    [InlineData("otro-sistema", "tenant-a")]
    [InlineData("rag.read.sistema", "tenant-ajeno")]
    [InlineData("rag.read.sistema", null)]
    [InlineData("rag.read.sistema", "")]
    [InlineData("RAG.READ.SISTEMA", "tenant-a")]
    [InlineData("rag.read.sistema", "TENANT-A")]
    public void Usuario_ajeno_por_scope_o_tenant_es_rechazado(string scope, string? tenant)
    {
        var actor = new CollectionActor { Scopes = [scope], Tenant = tenant };

        Assert.False(_authorization.Authorize(ProtectedCollection, EnterpriseActor(actor)));
    }

    [Fact]
    public void RequiredScopes_vacio_solo_permite_al_administrador()
    {
        var unpublished = ProtectedCollection with { RequiredScopes = [] };

        Assert.True(_authorization.Authorize(ProtectedCollection, EnterpriseActor(AuthorizedUser)));
        Assert.False(_authorization.Authorize(unpublished, EnterpriseActor(AuthorizedUser)));
        Assert.False(_authorization.Authorize(unpublished, EnterpriseActor(new CollectionActor())));
        Assert.True(_authorization.Authorize(unpublished, EnterpriseActor(Administrator)));
        Assert.False(_authorization.Authorize(unpublished with { Tenants = [] }, EnterpriseActor(AuthorizedUser)));
    }

    [Theory]
    [InlineData("operador")]
    [InlineData("evals")]
    public void Modo_local_explicito_lee_sin_IDP_los_mismos_resultados_que_admin_empresarial(string consumer)
    {
        var local = Resolver(AuthorizationMode.Local);
        var enterprise = Resolver(AuthorizationMode.Empresarial);
        var localActor = local.Resolve(() =>
            throw new InvalidOperationException($"{consumer} no dispone de IDP."));
        var identityReads = 0;
        var enterpriseAdmin = enterprise.Resolve(() =>
        {
            identityReads++;
            return new CollectionIdentity { IsAuthenticated = true, Actor = Administrator };
        });

        // Fixture de contenido protegido: ambas lecturas pasan por la misma matriz,
        // sin motor de retrieval ni una segunda regla ACL implementada dentro del test.
        string[] protectedContent = ["fragmento privado del sistema", "regla interna del tenant"];
        foreach (var manifest in new[] { ProtectedCollection, ProtectedCollection with { RequiredScopes = [] } })
        {
            var localAllowed = _authorization.Authorize(manifest, localActor);
            var adminAllowed = _authorization.Authorize(manifest, enterpriseAdmin);
            var localResults = localAllowed ? protectedContent : [];
            var adminResults = adminAllowed ? protectedContent : [];

            Assert.True(adminAllowed);
            Assert.Equal(adminAllowed, localAllowed);
            Assert.Equal(2, localResults.Length);
            Assert.Equal(adminResults, localResults);
            Assert.False(_authorization.Authorize(manifest, enterprise.Resolve(() => null)));
            Assert.False(_authorization.Authorize(manifest, enterprise.Resolve(() => new CollectionIdentity())));
        }

        Assert.Equal(1, identityReads);
    }

    [Fact]
    public void Empresarial_descarta_privilegios_incluso_admin_si_la_identidad_no_esta_autenticada()
    {
        var untrustedActors = new[] { Administrator, AuthorizedUser };
        foreach (var untrusted in untrustedActors)
        {
            var actor = Resolver(AuthorizationMode.Empresarial).Resolve(() =>
                new CollectionIdentity { IsAuthenticated = false, Actor = untrusted });

            Assert.False(actor.IsAdministrator);
            Assert.Empty(actor.Scopes);
            Assert.Null(actor.Tenant);
            Assert.False(_authorization.Authorize(ProtectedCollection, actor));
            Assert.False(_authorization.Authorize(ProtectedCollection with { RequiredScopes = [] }, actor));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empresarial_sin_identidad_o_sin_claims_no_obtiene_privilegios(bool authenticated)
    {
        var resolver = Resolver(AuthorizationMode.Empresarial);
        var actor = resolver.Resolve(() => authenticated
            ? new CollectionIdentity { IsAuthenticated = true }
            : null);

        Assert.False(actor.IsAdministrator);
        Assert.Empty(actor.Scopes);
        Assert.Null(actor.Tenant);
        Assert.False(_authorization.Authorize(ProtectedCollection, actor));
    }

    [Fact]
    public void Empresarial_no_convierte_un_fallo_de_identidad_en_fallback_local()
    {
        Assert.Throws<InvalidOperationException>(() => Resolver(AuthorizationMode.Empresarial)
            .Resolve(() => throw new InvalidOperationException("IDP no disponible")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tenant-ajeno")]
    public void Tenants_vacio_no_restringe_tenant_pero_sigue_exigiendo_scope(string? tenant)
    {
        var manifest = ProtectedCollection with { Tenants = [] };

        Assert.True(_authorization.Authorize(manifest,
            EnterpriseActor(AuthorizedUser with { Tenant = tenant })));
        Assert.False(_authorization.Authorize(manifest,
            EnterpriseActor(new CollectionActor { Tenant = tenant, Scopes = ["otro-sistema"] })));
        Assert.False(_authorization.Authorize(manifest, EnterpriseActor(new CollectionActor())));
    }

    [Fact]
    public void Manifiesto_ausente_solo_permite_al_administrador()
    {
        Assert.True(_authorization.Authorize(null, EnterpriseActor(Administrator)));
        Assert.False(_authorization.Authorize(null, EnterpriseActor(AuthorizedUser)));
        Assert.False(_authorization.Authorize(null, Resolver(AuthorizationMode.Empresarial).Resolve(() => null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Scope_vacio_o_blanco_no_es_un_permiso_aunque_el_manifiesto_lo_contenga(string scope)
    {
        var manifest = ProtectedCollection with { RequiredScopes = [scope] };

        Assert.False(_authorization.Authorize(manifest,
            EnterpriseActor(AuthorizedUser with { Scopes = [scope] })));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Local", true)]
    [InlineData("Empresarial", false)]
    public void Registro_DI_enlaza_el_modo_y_los_servicios_singleton(string? mode, bool expectedAccess)
    {
        using var provider = Services(mode);
        var resolver = provider.GetRequiredService<ICollectionActorResolver>();
        var authorization = provider.GetRequiredService<ICollectionAuthorizationService>();
        var identityReads = 0;
        var actor = resolver.Resolve(() =>
        {
            identityReads++;
            return null;
        });

        Assert.Same(resolver, provider.GetRequiredService<ICollectionActorResolver>());
        Assert.Same(authorization, provider.GetRequiredService<ICollectionAuthorizationService>());
        Assert.Equal(expectedAccess, authorization.Authorize(ProtectedCollection, actor));
        Assert.Equal(expectedAccess ? 0 : 1, identityReads);
    }

    [Fact]
    public void Modo_numerico_desconocido_falla_en_validacion_de_arranque()
    {
        using var provider = Services("42");

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Nombre_de_modo_desconocido_no_cae_en_local()
    {
        using var provider = Services("Empresaria");

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ICollectionActorResolver>().Resolve(() => null));
    }

    [Fact]
    public void Resolver_rechaza_modo_desconocido_aun_sin_validacion_DI()
    {
        Assert.Throws<InvalidOperationException>(() => Resolver((AuthorizationMode)42).Resolve(() =>
            throw new Xunit.Sdk.XunitException("No debe consultar identidad en un modo inválido.")));
    }

    private static CollectionActorResolver Resolver(AuthorizationMode mode) =>
        new(Options.Create(new CollectionAuthorizationOptions { Mode = mode }));

    private static CollectionActor EnterpriseActor(CollectionActor actor) =>
        Resolver(AuthorizationMode.Empresarial).Resolve(() =>
            new CollectionIdentity { IsAuthenticated = true, Actor = actor });

    private static ServiceProvider Services(string? mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            mode is null ? [] : new Dictionary<string, string?> { ["Authorization:Mode"] = mode }).Build();
        var services = new ServiceCollection();
        services.AddRagEngineCore(configuration);
        return services.BuildServiceProvider();
    }
}
