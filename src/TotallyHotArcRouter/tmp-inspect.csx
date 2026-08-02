using TotallyHotArcRouter.Hosting;
using TotallyHotArcRouter.Proxy;
using TotallyHotArcRouter.Models;
using TotallyHotArcRouter.Router;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var services = new ServiceCollection();
services.AddLogging();
services.AddOptions();
services.Configure<RoutingOptions>(_ => { });
services.AddSingleton<IRouterModelClient, StubRouterModelClient>();
services.AddTotallyHotArcRouter();

Console.WriteLine("OUTER SERVICES");
foreach (var d in services)
{
    var implType = d.ImplementationType?.FullName ?? "<null>";
    var isHosted = d.ServiceType == typeof(IHostedService)
        || typeof(IHostedService).IsAssignableFrom(d.ServiceType)
        || (d.ImplementationType is not null && typeof(IHostedService).IsAssignableFrom(d.ImplementationType))
        || (d.ImplementationInstance is IHostedService)
        || (d.ImplementationFactory is not null && d.ServiceType == typeof(IHostedService));
    Console.WriteLine($"{d.ServiceType.FullName} | {implType} | hosted={isHosted} | lifetime={d.Lifetime}");
}

sealed class StubRouterModelClient : IRouterModelClient
{
    public Task<string> GetResponseAsync(string model, string prompt, CancellationToken cancellationToken = default) => Task.FromResult("ok");
}

