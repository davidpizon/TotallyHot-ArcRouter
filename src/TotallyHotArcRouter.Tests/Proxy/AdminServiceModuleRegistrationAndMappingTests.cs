using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Drives <see cref="IAdminServiceModule.Register"/> and <see cref="IAdminServiceModule.Map"/> through a real
/// <see cref="WebApplication"/> for every optional admin group, closing the gap
/// <see cref="AdminServiceModuleTests"/> leaves open: that suite only proves a group reaches
/// <see cref="ProxyServerDependencies.AdminModules"/>, never that <c>Register</c> supplies every collaborator
/// its gRPC service constructor needs or that <c>Map</c> actually publishes an endpoint. A missing
/// registration or a mismatched <c>MapGrpcService&lt;T&gt;</c> call would pass that suite and only fail on
/// the first RPC - exactly the drift ADR-0010's module seam exists to prevent.
/// </summary>
public sealed class AdminServiceModuleRegistrationAndMappingTests
{
    private static readonly (Type GroupType, Type ServiceType)[] Modules =
    [
        (typeof(PriceSourceAdminDependencies), typeof(PriceSourceAdminGrpcService)),
        (typeof(BenchmarkDataAdminDependencies), typeof(BenchmarkDataAdminGrpcService)),
        (typeof(LlmRouterModelAdminDependencies), typeof(LlmRouterModelAdminGrpcService)),
        (typeof(ClusterModelAdminDependencies), typeof(ClusterModelAdminGrpcService)),
        (typeof(LogRegModelAdminDependencies), typeof(LogRegModelAdminGrpcService)),
        (typeof(RouterSettingsAdminDependencies), typeof(RouterSettingsAdminGrpcService)),
        (typeof(CostReconciliationAdminDependencies), typeof(CostReconciliationAdminGrpcService))
    ];

    public static TheoryData<string> ModuleGroupNames()
    {
        var data = new TheoryData<string>();
        foreach (var (groupType, _) in Modules) data.Add(groupType.Name);

        return data;
    }

    /// <summary>
    /// Guards <see cref="Modules"/> itself against drift: it is a hand-written list, kept separate from
    /// <see cref="AdminServiceModuleTests"/>'s reflection-discovered one so this suite can pair each group
    /// with the gRPC service type reflection alone cannot name. Without this check, a new
    /// <c>*AdminDependencies</c> record that implements <see cref="IAdminServiceModule"/> would be picked up
    /// by <see cref="AdminServiceModuleTests"/>'s registry test and silently miss this one - the exact
    /// missing-registration/mapping gap this suite exists to catch.
    /// </summary>
    [Fact]
    public void Modules_covers_every_group_that_implements_the_module_seam()
    {
        var reflectedGroupTypes = typeof(ProxyServerDependencies)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(IAdminServiceModule).IsAssignableFrom(p.PropertyType))
            .Select(p => p.PropertyType)
            .ToArray();

        reflectedGroupTypes.Should().BeEquivalentTo(Modules.Select(m => m.GroupType),
            "every ProxyServerDependencies property typed as IAdminServiceModule must have a paired "
            + "gRPC service type here, or Register/Map for it are never exercised by this suite");
    }

    [Theory]
    [MemberData(nameof(ModuleGroupNames))]
    public void Register_and_Map_wire_everything_the_grpc_service_construction_needs(string groupTypeName)
    {
        var (groupType, serviceType) = Modules.Single(m => m.GroupType.Name == groupTypeName);
        var module = CreatePopulatedModule(groupType);

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGrpc();
        builder.Services.AddLogging();

        // LogRegModelAdminGrpcService's third constructor dependency, IOptions<RoutingOptions>, is not a
        // module collaborator - ProxyServerDependencies.RoutingOptions registers it unconditionally instead
        // (see that record's remarks) because RoutingModeAdminGrpcService needs it whether or not this group
        // is supplied. Mirror that registration here so the smoke reflects what ProxyServer actually wires,
        // not an artificially stricter standalone module.
        builder.Services.AddSingleton(Options.Create(new RoutingOptions()));

        module.Register(builder.Services);

        using var app = builder.Build();
        module.Map(app);

        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).Should().NotBeEmpty(
            $"{groupType.Name}.Map should publish at least one endpoint for {serviceType.Name}");

        // Constructing the real gRPC service type through the app's container is the actual regression this
        // seam guards against: MapGrpcService<T> only reflects over T, it never constructs it, so a missing
        // AddSingleton in Register maps successfully and only throws on the service's first RPC.
        var service = ActivatorUtilities.CreateInstance(app.Services, serviceType);
        service.Should().BeOfType(serviceType);
    }

    /// <summary>
    /// Builds a module instance with every collaborator populated so <c>Register</c> can run its real
    /// <c>AddSingleton</c> calls without hitting a null reference. Concrete collaborator types are built with
    /// <see cref="RuntimeHelpers.GetUninitializedObject"/> - the same trick <see cref="AdminServiceModuleTests"/>
    /// already uses for the group itself - because <c>Register</c> only ever passes these instances to the DI
    /// container, never calls a member on them; interface-typed collaborators are Moq stubs, since
    /// <c>GetUninitializedObject</c> cannot instantiate an interface.
    /// </summary>
    private static IAdminServiceModule CreatePopulatedModule(Type groupType)
    {
        var group = RuntimeHelpers.GetUninitializedObject(groupType);

        foreach (var property in groupType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite) continue;

            property.SetValue(group, CreateCollaborator(property.PropertyType));
        }

        return (IAdminServiceModule)group;
    }

    private static object CreateCollaborator(Type type)
    {
        // IOptions<T>'s constructors read .Value's members directly (e.g. StorageOptions.ResolveClusterModelPath),
        // so T needs its real field initializers (default paths, etc.) rather than the all-zero/all-null shape
        // GetUninitializedObject would give it - Activator.CreateInstance runs those initializers.
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IOptions<>))
        {
            var valueType = type.GetGenericArguments()[0];
            var value = Activator.CreateInstance(valueType)!;
            var create = typeof(Options).GetMethod(nameof(Options.Create))!.MakeGenericMethod(valueType);

            return create.Invoke(obj: null, parameters: [value])!;
        }

        if (type.IsInterface)
        {
            dynamic mock = Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!;
            return mock.Object;
        }

        return RuntimeHelpers.GetUninitializedObject(type);
    }
}
