using AwesomeAssertions;
using System.Reflection;
using System.Runtime.CompilerServices;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Guards the <see cref="IAdminServiceModule"/> registration seam that <see cref="ProxyServer"/> drives.
/// </summary>
/// <remarks>
/// The failure these exist to catch is specific and silent: <c>MapGrpcService</c> only reflects over the
/// service type, it never constructs it, so an admin feature whose group never reaches
/// <see cref="ProxyServerDependencies.AdminModules"/> does not fail at startup - it simply has no endpoint,
/// and the Governance panel reports the router as unreachable. Nothing else in the suite notices, because
/// every other test drives the services directly rather than through the host.
/// </remarks>
public sealed class AdminServiceModuleTests
{
    /// <summary>
    /// Every <see cref="ProxyServerDependencies"/> property whose group opted into the module seam, found by
    /// reflection rather than listed here - a hand-maintained list would need the same edit the thing it is
    /// guarding needs, so it would go stale in exactly the case that matters.
    /// </summary>
    public static TheoryData<string> ModuleProperties()
    {
        var data = new TheoryData<string>();
        foreach (var property in ModulePropertyInfos()) data.Add(property.Name);

        return data;
    }

    private static IEnumerable<PropertyInfo> ModulePropertyInfos()
    {
        return typeof(ProxyServerDependencies)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(IAdminServiceModule).IsAssignableFrom(p.PropertyType));
    }

    [Theory]
    [MemberData(nameof(ModuleProperties))]
    public void Every_group_that_implements_the_module_seam_is_surfaced_by_AdminModules(string propertyName)
    {
        var property = ModulePropertyInfos().Single(p => p.Name == propertyName);

        // Uninitialized rather than constructed: this asserts the plumbing carries the instance through,
        // and constructing a real group would drag in a training service, a transcript store, and a
        // SQLite database for a question none of them bear on.
        var group = RuntimeHelpers.GetUninitializedObject(property.PropertyType);
        var dependencies = new ProxyServerDependencies();
        property.SetValue(obj: dependencies, value: group);

        dependencies.AdminModules.Should().Contain(
            (IAdminServiceModule)group,
            $"{propertyName} implements IAdminServiceModule but ProxyServerDependencies.AdminModules never "
            + "yields it, so its gRPC service would silently have no endpoint");
    }

    [Fact]
    public void A_dependencies_object_with_no_optional_groups_yields_no_modules()
    {
        // The default path for a test that only exercises forwarding: no admin feature is supplied, so
        // nothing is registered or mapped and the inner host still starts.
        new ProxyServerDependencies().AdminModules.Should().BeEmpty();
    }

    [Fact]
    public void The_unconditionally_mapped_groups_stay_off_the_module_seam()
    {
        // These map whether or not their group was supplied, falling back to a null-object collaborator.
        // A module cannot register itself when it does not exist, so pulling them onto the seam would
        // change *when* they map - see ProxyServerDependencies.AdminModules' remarks and ADR-0010.
        string[] unconditional =
            ["UpdateAdmin", "RoutingGateAdmin", "RegretHarnessAdmin", "JudgeCalibrationAdmin"];

        ModulePropertyInfos().Select(p => p.Name).Should().NotIntersectWith(unconditional);
    }
}
