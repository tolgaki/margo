using Margo.Cloud.Router.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Router;

/// <summary>Wires the router into an ASP.NET Core application. Python: <c>build_app</c>.</summary>
public static class RouterApplication
{
    /// <summary>The logger category of the router's own messages (Python: <c>margo.router</c>).</summary>
    public const string LogCategory = "margo.router";

    /// <summary>The logger category of the token check (Python: the Agents SDK's loggers).</summary>
    public const string AuthLogCategory = "margo.router.auth";

    /// <summary>
    /// Register the router. <paramref name="routes"/>, <paramref name="queue"/> and
    /// <paramref name="jwks"/> are registered as services, so a host (or a test) can replace them.
    /// </summary>
    public static IServiceCollection AddMargoRouter(this IServiceCollection services, AgentAuthSettings auth, IRouteSource routes,
        IProvisioningSink queue, IJwksSource jwks, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(jwks);
        var time = timeProvider ?? TimeProvider.System;
        services.AddSingleton(auth);
        services.AddSingleton(routes);
        services.AddSingleton(queue);
        services.AddSingleton(jwks);
        services.AddSingleton(provider => new JwksClientManager(provider.GetRequiredService<IJwksSource>(), time));
        services.AddSingleton(provider => new AgentTokenValidator(provider.GetRequiredService<AgentAuthSettings>(),
            provider.GetRequiredService<JwksClientManager>(), provider.GetRequiredService<ILoggerFactory>().CreateLogger(AuthLogCategory), time));
        services.AddSingleton(provider => new RouteTableCache(provider.GetRequiredService<IRouteSource>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory), time));
        services.AddSingleton<InstanceConnector>();
        services.AddSingleton(provider => new RouterService(provider.GetRequiredService<RouteTableCache>(),
            provider.GetRequiredService<IProvisioningSink>(), provider.GetRequiredService<AgentTokenValidator>(),
            provider.GetRequiredService<InstanceConnector>(), provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)));
        return services;
    }

    /// <summary>Serve every request with the router.</summary>
    public static WebApplication UseMargoRouter(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = InstanceConnector.Heartbeat, KeepAliveTimeout = InstanceConnector.Heartbeat / 2 });
        var router = app.Services.GetRequiredService<RouterService>();
        app.Run(router.HandleAsync);
        return app;
    }
}
